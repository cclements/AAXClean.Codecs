using AAXClean.FrameFilters;
using System;
using System.IO;

namespace AAXClean.Codecs.FrameFilters.Audio;

internal sealed class PresentationWindowFilter : FrameTransformBase<WaveEntry, WaveEntry>
{
	protected override int InputBufferSize => 100;
	private readonly WaveFormat waveFormat;
	private readonly long windowStart;
	private readonly long windowEnd;
	private readonly long presentationOrigin;

	public PresentationWindowFilter(WaveFormat waveFormat, long windowStart, long windowEnd, long presentationOrigin)
	{
		ArgumentNullException.ThrowIfNull(waveFormat);
		if (windowStart < 0)
			throw new ArgumentOutOfRangeException(nameof(windowStart));
		if (windowEnd < windowStart)
			throw new ArgumentOutOfRangeException(nameof(windowEnd));
		if (presentationOrigin < 0 || presentationOrigin > windowStart)
			throw new ArgumentOutOfRangeException(nameof(presentationOrigin));

		this.waveFormat = waveFormat;
		this.windowStart = windowStart;
		this.windowEnd = windowEnd;
		this.presentationOrigin = presentationOrigin;
	}

	public override WaveEntry PerformFiltering(WaveEntry input)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (input.SamplesInFrame == 0)
			return Copy(input, input.StartSample is long emptyStart ? emptyStart - presentationOrigin : null, 0, Memory<byte>.Empty, Memory<byte>.Empty);
		if (input.StartSample is not long inputStart)
			throw new InvalidDataException("Decoded PCM cannot be cropped without its source media coordinate.");

		long inputEnd = checked(inputStart + input.SamplesInFrame);
		long keptStart = Math.Max(inputStart, windowStart);
		long keptEnd = Math.Min(inputEnd, windowEnd);
		if (keptEnd <= keptStart)
			return Copy(input, keptStart - presentationOrigin, 0, Memory<byte>.Empty, Memory<byte>.Empty);

		int bytesPerSample = input.FrameData2.IsEmpty
			? waveFormat.BlockAlign
			: waveFormat.BlockAlign / waveFormat.Channels;
		long requiredBytes = checked((long)input.SamplesInFrame * bytesPerSample);
		if (input.FrameData.Length < requiredBytes
			|| (!input.FrameData2.IsEmpty && input.FrameData2.Length < requiredBytes))
			throw new InvalidDataException("Decoded PCM buffer is shorter than its declared sample count.");

		int skipSamples = checked((int)(keptStart - inputStart));
		int keptSamples = checked((int)(keptEnd - keptStart));
		int byteOffset = checked(skipSamples * bytesPerSample);
		int byteCount = checked(keptSamples * bytesPerSample);
		var data = input.FrameData.Slice(byteOffset, byteCount);
		var data2 = input.FrameData2.IsEmpty ? Memory<byte>.Empty : input.FrameData2.Slice(byteOffset, byteCount);

		return Copy(input, keptStart - presentationOrigin, (uint)keptSamples, data, data2);
	}

	private static WaveEntry Copy(WaveEntry input, long? startSample, uint samplesInFrame, Memory<byte> data, Memory<byte> data2)
		=> new()
		{
			Chunk = input.Chunk,
			SamplesInFrame = samplesInFrame,
			FrameData = data,
			FrameData2 = data2,
			Encoding = input.Encoding,
			ExtraData = input.ExtraData,
			IsSyncSample = input.IsSyncSample,
			StartSample = startSample,
		};
}
