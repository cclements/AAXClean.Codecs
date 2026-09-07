using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.Codecs.Interop;
using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AAXClean.Codecs;

internal sealed class FfmpegAacDecoder : IDisposable
{
	internal const string libname = "aaxcleannative";
	public WaveFormat WaveFormat { get; }
	private readonly NativeDecode AudioDecoder;
	private readonly uint InputTimescale;
	private readonly Queue<FrameEntry> PendingMetadata = new();
	private FrameEntry? CurrentSource;
	private long? NextOutputStartSample;
	private bool HasOutput, InputEnded, OutputEnded, Disposed;
	private long ReceiveProgress;
	private int EmptyReceives;
	// A broken codec must not keep a cancellation-insensitive zero-output loop alive.
	private const int MaxEmptyReceives = 1024;
	private int NumberOfSamplesSkipped;
	private int MaxSamplesToSkip { get; }
	private static TimeSpan MaxTimeToSkip { get; } = TimeSpan.FromSeconds(1);

	public FfmpegAacDecoder(AudioSampleEntry audioSampleEntry, uint inputTimescale, WaveFormatEncoding waveFormatEncoding)
	{
		ArgumentOutOfRangeException.ThrowIfZero(inputTimescale);
		InputTimescale = inputTimescale;
		NativeDecode.EnsureDrainApi();
		if (audioSampleEntry.Esds is EsdsBox esds)
		{
			var asc = esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig;
			MaxSamplesToSkip = GetMaxNumberOfSamplesToSkip(esds);
			WaveFormat = new WaveFormat((SampleRate)asc.SamplingFrequency, waveFormatEncoding, asc.ChannelConfiguration == 2);
			AudioDecoder = new NativeAacDecode(esds, WaveFormat);
		}
		else if (audioSampleEntry.Dec3 is Dec3Box dec3)
		{
			WaveFormat = new WaveFormat((SampleRate)dec3.SampleRate, waveFormatEncoding, stereo: true);
			AudioDecoder = new NativeEc3Decode(dec3, WaveFormat);
		}
		else if (audioSampleEntry.Dac4 is Dac4Box dac4)
		{
			WaveFormat = new WaveFormat((SampleRate?)dac4.SampleRate ?? SampleRate.Hz_44100, waveFormatEncoding, stereo: true);
			AudioDecoder = new NativeAc4Decode(dac4, WaveFormat);
		}
		else
			throw new Exception($"AudioSampleEntry does not contain {nameof(EsdsBox)} or {nameof(Dec3Box)}");
	}

	public FfmpegAacDecoder(AudioSampleEntry audioSampleEntry, uint inputTimescale, WaveFormatEncoding waveFormatEncoding, SampleRate sampleRate, bool stereo)
	{
		ArgumentOutOfRangeException.ThrowIfZero(inputTimescale);
		InputTimescale = inputTimescale;
		NativeDecode.EnsureDrainApi();
		WaveFormat = new WaveFormat(sampleRate, waveFormatEncoding, stereo);
		if (audioSampleEntry.Esds is EsdsBox esds)
		{
			MaxSamplesToSkip = GetMaxNumberOfSamplesToSkip(esds);
			AudioDecoder = new NativeAacDecode(esds, WaveFormat);
		}
		else if (audioSampleEntry.Dec3 is Dec3Box dec3)
			AudioDecoder = new NativeEc3Decode(dec3, WaveFormat);
		else if (audioSampleEntry.Dac4 is Dac4Box dac4)
			AudioDecoder = new NativeAc4Decode(dac4, WaveFormat);
		else
			throw new Exception($"AudioSampleEntry does not contain {nameof(EsdsBox)} or {nameof(Dec3Box)}");

	}


	// Narrow transport seam for protocol/lifecycle tests; production constructors
	// above require the matched native protocol before opening resources.
	internal FfmpegAacDecoder(NativeDecode decoder, WaveFormat format, uint inputTimescale)
	{
		ArgumentNullException.ThrowIfNull(decoder);
		ArgumentNullException.ThrowIfNull(format);
		ArgumentOutOfRangeException.ThrowIfZero(inputTimescale);
		AudioDecoder = decoder;
		WaveFormat = format;
		InputTimescale = inputTimescale;
	}

	private static int GetMaxNumberOfSamplesToSkip(EsdsBox esds)
		=> esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AudioObjectType == 42
		? (int)(esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.SamplingFrequency * MaxTimeToSkip.TotalSeconds)
		: 0;

	public IEnumerable<WaveEntry> DecodeWave(FrameEntry input, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Disposed, this);
		ArgumentNullException.ThrowIfNull(input);
		if (InputEnded || OutputEnded)
			throw new InvalidOperationException("Decoder input has already ended.");
		if (input.FrameData.IsEmpty)
			throw new InvalidDataException("An empty compressed frame is not an end-of-input marker.");

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int sent = AudioDecoder.SubmitPacket(input.FrameData);
			if (sent == NativeDecode.Accepted)
				break;
			if (sent != NativeDecode.ReceiveFirst)
			{
				if (TrySkipInitialUsacError(sent, input))
					yield break;
				throw ProtocolError("submitting compressed input", sent);
			}
			long before = ReceiveProgress;
			foreach (var output in ReceiveAvailable(null, cancellationToken))
				yield return output;
			if (OutputEnded || ReceiveProgress == before)
				throw new InvalidDataException("Decoder rejected a packet but could not receive output; the same packet remains unaccepted.");
			// Retry the same borrowed bytes only after the receive side made progress.
		}

		RememberSource(input);
		EmptyReceives = 0;
		foreach (var output in ReceiveAvailable(input, cancellationToken))
			yield return output;
	}

	public IEnumerable<WaveEntry> DecodeFlush(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(Disposed, this);
		if (OutputEnded)
			yield break;
		while (!InputEnded)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int sent = AudioDecoder.FinishInput();
			if (sent == NativeDecode.Accepted)
			{
				InputEnded = true;
				break;
			}
			if (sent != NativeDecode.ReceiveFirst)
				throw ProtocolError("submitting decoder EOF", sent);
			long before = ReceiveProgress;
			foreach (var output in ReceiveAvailable(null, cancellationToken))
				yield return output;
			if (OutputEnded || ReceiveProgress == before)
				throw new InvalidDataException("Decoder could neither accept EOF nor receive output.");
		}
		foreach (var output in ReceiveAvailable(null, cancellationToken))
			yield return output;
		if (!OutputEnded)
			throw new InvalidDataException("Decoder requested more input after accepting EOF.");
	}

	private IEnumerable<WaveEntry> ReceiveAvailable(FrameEntry? seedInput, CancellationToken cancellationToken)
	{
		while (!OutputEnded)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int state = AudioDecoder.ReceivePcm(Span<byte>.Empty, Span<byte>.Empty, 0, out int required);
			if (state == NativeDecode.NeedInput)
				yield break;
			if (state == NativeDecode.EndOfStream)
			{
				OutputEnded = true;
				PendingMetadata.Clear();
				yield break;
			}
			if (state == NativeDecode.PcmConsumed)
			{
				CountEmptyReceive();
				continue;
			}
			if (state != NativeDecode.PcmReady || required <= 0)
			{
				if (TrySkipInitialUsacError(state, seedInput))
					yield break;
				throw ProtocolError("querying decoded PCM", state);
			}
			Memory<byte> decoded = new byte[checked(required * WaveFormat.BlockAlign)];
			bool planar = WaveFormat.Encoding is NAudio.Wave.WaveFormatEncoding.Dts && WaveFormat.Channels == 2;
			int planeLength = planar ? decoded.Length / 2 : decoded.Length;
			state = AudioDecoder.ReceivePcm(decoded.Span[..planeLength],
				planar ? decoded.Span[planeLength..] : Span<byte>.Empty, required, out int received);
			if (state == NativeDecode.EndOfStream && received == 0)
			{
				OutputEnded = true;
				PendingMetadata.Clear();
				yield break;
			}
			if (state != NativeDecode.PcmConsumed || received < 0 || received > required)
				throw ProtocolError("receiving decoded PCM", state);
			ReceiveProgress++;
			if (received == 0)
			{
				CountEmptyReceive();
				continue;
			}
			EmptyReceives = 0;
			HasOutput = true;
			foreach (var output in PositionOutput(decoded, planar, planeLength, received))
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return output;
			}
		}
	}

	private void CountEmptyReceive()
	{
		ReceiveProgress++;
		if (++EmptyReceives > MaxEmptyReceives)
			throw new InvalidDataException("Decoder produced too many consecutive empty frames without PCM progress.");
	}

	private bool TrySkipInitialUsacError(int error, FrameEntry? input)
	{
		// Preserve the existing bounded USAC startup allowance, before any PCM was
		// delivered. Once audio exists, an error must terminate instead of hiding a gap.
		if (error != -1313558101 || input is null || HasOutput || MaxSamplesToSkip == 0)
			return false;
		long skipped = checked((long)NumberOfSamplesSkipped + input.SamplesInFrame);
		if (skipped >= MaxSamplesToSkip)
			return false;
		NumberOfSamplesSkipped = (int)skipped;
		PendingMetadata.Clear();
		CurrentSource = null;
		NextOutputStartSample = null;
		return true;
	}

	private void RememberSource(FrameEntry input)
	{
		// Metadata can outlive several zero-output packets. Do not retain their
		// compressed payloads, and do not assume one packet produces one PCM frame.
		var metadata = new FrameEntry
		{
			Chunk = input.Chunk, ExtraData = input.ExtraData, IsSyncSample = input.IsSyncSample,
			StartSample = input.StartSample, SamplesInFrame = input.SamplesInFrame,
			FrameData = Memory<byte>.Empty,
		};
		if (CurrentSource is null)
			CurrentSource = metadata;
		else
			PendingMetadata.Enqueue(metadata);
	}

	private IEnumerable<WaveEntry> PositionOutput(Memory<byte> data, bool planar, int planeLength, int count)
	{
		NextOutputStartSample ??= ScaleSource(CurrentSource?.StartSample);
		int bytesPerSample = planar ? WaveFormat.BlockAlign / 2 : WaveFormat.BlockAlign;
		int offset = 0;
		while (offset < count)
		{
			long? start = NextOutputStartSample;
			while (PendingMetadata.TryPeek(out var next) &&
				start is long positioned && ScaleSource(next.StartSample) is long nextStart && nextStart <= positioned)
				CurrentSource = PendingMetadata.Dequeue();
			int take = count - offset;
			if (start is long outputStart && PendingMetadata.TryPeek(out var pending) &&
				ScaleSource(pending.StartSample) is long boundary && boundary > outputStart)
				take = (int)Math.Min(take, boundary - outputStart);
			var source = CurrentSource;
			if (start is long known)
				NextOutputStartSample = checked(known + take);
			yield return new WaveEntry
			{
				Chunk = source?.Chunk, ExtraData = source?.ExtraData, IsSyncSample = source?.IsSyncSample,
				Encoding = (WaveFormatEncoding)WaveFormat.Encoding,
				StartSample = start, SamplesInFrame = (uint)take,
				FrameData = data.Slice(offset * bytesPerSample, take * bytesPerSample),
				FrameData2 = planar ? data.Slice(planeLength + offset * bytesPerSample, take * bytesPerSample) : Memory<byte>.Empty,
			};
			offset += take;
		}
	}

	private long? ScaleSource(long? source) => source is long sample
		? ScaleSamplePosition(sample, InputTimescale, (uint)WaveFormat.SampleRate) : null;
	internal static long ScaleSamplePosition(long sample, uint inputTimescale, uint outputSampleRate)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(sample);
		ArgumentOutOfRangeException.ThrowIfZero(inputTimescale);
		ArgumentOutOfRangeException.ThrowIfZero(outputSampleRate);
		return checked((long)ElstBox.ScaleDuration((ulong)sample, inputTimescale, outputSampleRate));
	}
	private static Exception ProtocolError(string operation, int error)
		=> new InvalidDataException($"Error {operation}. Native status {error} ({NativeDecode.GetFFmpegErrorString(error)}).");
	public void Dispose()
	{
		if (Disposed) return;
		Disposed = true;
		PendingMetadata.Clear();
		CurrentSource = null;
		AudioDecoder.Dispose();
	}
}
