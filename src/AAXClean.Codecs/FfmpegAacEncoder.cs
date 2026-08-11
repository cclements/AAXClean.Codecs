using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.Codecs.Interop;
using AAXClean.FrameFilters;
using System;
using System.Collections.Generic;
using System.IO;

namespace AAXClean.Codecs;

internal unsafe sealed class FfmpegAacEncoder : IDisposable
{
	internal const string libname = FfmpegAacDecoder.libname;
	public WaveFormat WaveFormat { get; }
	private readonly NativeAacEncode AacEncoder;
	private const int AAC_SAMPLES_PER_FRAME = 1024;
	private bool FlushStarted;
	public long AcceptedPcmSamples { get; private set; }
	public long PaddedPcmSamples { get; private set; }
	public long EncodedMediaSamples { get; private set; }
	public long PresentationStartSamples { get; private set; }
	public byte[] GetAudioSpecificConfig() => AacEncoder.GetAudioSpecificConfig();

	public FfmpegAacEncoder(WaveFormat inputWaveFormat, long? bitRate, double? quality)
	{
		if (inputWaveFormat.Channels > 2)
			throw new ArgumentException("AAC encoder only supports mono or stereo wave formats.", nameof(inputWaveFormat));
		if (inputWaveFormat.Encoding != NAudio.Wave.WaveFormatEncoding.Pcm)
			throw new ArgumentException("AAC encoder only supports PCM wave formats.", nameof(inputWaveFormat));

		WaveFormat = inputWaveFormat;
		AacEncoder = new NativeAacEncode(WaveFormat, bitRate ?? 0, quality ?? 0);
	}

	public IEnumerable<FrameEntry> EncodeWave(WaveEntry input)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (FlushStarted)
			throw new InvalidOperationException("Cannot encode PCM after the AAC encoder has started flushing.");
		if (!input.FrameData2.IsEmpty)
			throw new NotSupportedException("AAC encoding supports only packed PCM; planar FrameData2 input cannot be encoded safely.");
		if (input.SamplesInFrame > int.MaxValue)
			throw new InvalidDataException("PCM frame sample count exceeds the supported size.");

		int bytesPerSample = WaveFormat.BlockAlign;
		long requiredBytes = checked((long)input.SamplesInFrame * bytesPerSample);
		if (input.FrameData.Length < requiredBytes)
			throw new InvalidDataException("PCM buffer is shorter than its declared sample count.");

		int startIndex = 0;
		var frameSize = (int)input.SamplesInFrame;

		//It's possible that a frame may be larger than AAC_SAMPLES_PER_FRAME
		//Send a maximum of AAC_SAMPLES_PER_FRAME at a time to the encoder.
		while (frameSize > 0)
		{
			int toSend = Math.Min(frameSize, AAC_SAMPLES_PER_FRAME);
			int byteOffset = checked(startIndex * bytesPerSample);
			int byteCount = checked(toSend * bytesPerSample);

			int samplesNeeded = SendSamples(input.FrameData.Slice(byteOffset, byteCount).Span, toSend);
			AcceptedPcmSamples = checked(AcceptedPcmSamples + toSend);
			startIndex += toSend;
			frameSize -= toSend;

			if (samplesNeeded == 0)
			{
				foreach (var encodedFrame in DrainAvailableFrames(input))
					yield return encodedFrame;
			}
		}
	}

	public IEnumerable<FrameEntry> EncodeFlush()
	{
		if (FlushStarted)
			throw new InvalidOperationException("AAC encoder can only be flushed once.");
		FlushStarted = true;

		int paddingSamples = (int)((AAC_SAMPLES_PER_FRAME - AcceptedPcmSamples % AAC_SAMPLES_PER_FRAME) % AAC_SAMPLES_PER_FRAME);
		PaddedPcmSamples = checked(AcceptedPcmSamples + paddingSamples);
		if (paddingSamples > 0)
		{
			var zeroPcm = new byte[checked(paddingSamples * WaveFormat.BlockAlign)];
			int samplesNeeded = SendSamples(zeroPcm, paddingSamples);

			if (samplesNeeded != 0)
				throw new InvalidDataException("AAC encoder did not accept the complete zero-padded final frame.");

			foreach (var encodedFrame in DrainAvailableFrames(null))
				yield return encodedFrame;
		}

		int ret = AacEncoder.EncodeFlush();

		if (ret < 0)
			throw new Exception($"Error flushing AAC encoder.");

		foreach (var encodedFrame in DrainAvailableFrames(null))
			yield return encodedFrame;

		if (EncodedMediaSamples < PaddedPcmSamples)
			throw new InvalidDataException("AAC encoder emitted less media than the padded PCM it accepted.");

		long observableInitialPadding = EncodedMediaSamples - PaddedPcmSamples;
		if (AacEncoder.TryGetInitialPadding() is int nativeInitialPadding)
		{
			if (nativeInitialPadding > EncodedMediaSamples
				|| AcceptedPcmSamples > EncodedMediaSamples - nativeInitialPadding)
				throw new InvalidDataException(
					$"AAC encoder reported {nativeInitialPadding} initial-padding samples, which leaves insufficient media for the {AcceptedPcmSamples} accepted PCM samples.");

			PresentationStartSamples = nativeInitialPadding;
		}
		else
		{
			// Released native binaries predate the initial-padding ABI. The explicitly
			// zero-padded input makes total encoded media minus padded input the only
			// observable estimate available without breaking those runtimes.
			PresentationStartSamples = observableInitialPadding;
		}
	}

	private IEnumerable<FrameEntry> DrainAvailableFrames(FrameEntry? input)
	{
		int encodedSize;
		while ((encodedSize = GetAvailableFrameSize()) > 0)
		{
			Memory<byte> encodedAudio = GetEncodedFrame(encodedSize);
			EncodedMediaSamples = checked(EncodedMediaSamples + AAC_SAMPLES_PER_FRAME);
			yield return new FrameEntry
			{
				Chunk = input?.Chunk,
				SamplesInFrame = AAC_SAMPLES_PER_FRAME,
				FrameData = encodedAudio
			};
		}
		if (encodedSize < 0)
			throw new Exception("Failed to retrieve encoded samples.");
	}

	private int SendSamples(Span<byte> frameData, int numSamples)
	{
		int ret;
		fixed (byte* buffer1 = frameData)
		{
			ret = AacEncoder.EncodeFrame(buffer1, null, numSamples);
		}

		if (ret < 0)
			throw new Exception("Failed to encode samples.");

		return ret;
	}

	private int GetAvailableFrameSize() => AacEncoder.ReceiveEncodedFrame(null, 0);

	private Memory<byte> GetEncodedFrame(int encodedSize)
	{
		Memory<byte> encAud = new byte[encodedSize];
		fixed (byte* pEncAud = encAud.Span)
		{
			encodedSize = AacEncoder.ReceiveEncodedFrame(pEncAud, encodedSize);
		}
		if (encodedSize != 0)
			throw new Exception("Failed to retrieve encoded samples.");
		return encAud;
	}

	public void Dispose()
	{
		AacEncoder.Dispose();
	}
}
