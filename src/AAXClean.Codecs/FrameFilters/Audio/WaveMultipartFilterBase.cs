using AAXClean.FrameFilters.Audio;
using Mpeg4Lib;
using System;
using System.IO;

namespace AAXClean.Codecs.FrameFilters.Audio;

/// <summary>
/// Multipart sink base for decoded PCM. Unlike compressed-frame sinks, PCM may be
/// divided losslessly so every part receives exactly its half-open chapter interval.
/// </summary>
internal abstract class WaveMultipartFilterBase<TCallback>
	: MultipartFilterBase<WaveEntry, TCallback>
	where TCallback : INewSplitCallback<TCallback>
{
	private readonly WaveFormat waveFormat;

	protected WaveMultipartFilterBase(
		ChapterInfo splitChapters,
		WaveFormat waveFormat,
		Func<TimeSpan, long> presentationTimeToSample)
		: base(
			splitChapters,
			waveFormat.SampleRateEnum,
			waveFormat.Channels == 2,
			presentationTimeToSample,
			PresentationTimeMappingKind.Exact)
	{
		ArgumentNullException.ThrowIfNull(waveFormat);
		ArgumentNullException.ThrowIfNull(presentationTimeToSample);
		this.waveFormat = waveFormat;
	}

	protected sealed override bool SplitFramesAtPartBoundaries => true;
	protected sealed override bool IsEmptyPlaceholder(WaveEntry frame)
		=> base.IsEmptyPlaceholder(frame) && frame.FrameData2.IsEmpty;

	protected sealed override (WaveEntry first, WaveEntry second) SplitFrame(
		WaveEntry input,
		uint firstPartSamples)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (firstPartSamples == 0 || firstPartSamples >= input.SamplesInFrame)
			throw new ArgumentOutOfRangeException(nameof(firstPartSamples));

		bool planar = !input.FrameData2.IsEmpty;
		int bytesPerSample = planar
			? waveFormat.BlockAlign / waveFormat.Channels
			: waveFormat.BlockAlign;
		int requiredBytes = checked((int)input.SamplesInFrame * bytesPerSample);
		if (input.FrameData.Length != requiredBytes
			|| (planar && input.FrameData2.Length != requiredBytes))
			throw new InvalidDataException("Decoded PCM buffer length does not match its declared sample count.");

		int firstBytes = checked((int)firstPartSamples * bytesPerSample);
		uint secondPartSamples = input.SamplesInFrame - firstPartSamples;
		long? secondStart = input.StartSample is long start
			? checked(start + firstPartSamples)
			: null;

		WaveEntry first = Copy(
			input,
			input.StartSample,
			firstPartSamples,
			input.FrameData[..firstBytes],
			planar ? input.FrameData2[..firstBytes] : Memory<byte>.Empty);
		WaveEntry second = Copy(
			input,
			secondStart,
			secondPartSamples,
			input.FrameData[firstBytes..requiredBytes],
			planar ? input.FrameData2[firstBytes..requiredBytes] : Memory<byte>.Empty);

		return (first, second);
	}

	private static WaveEntry Copy(
		WaveEntry input,
		long? startSample,
		uint samplesInFrame,
		Memory<byte> data,
		Memory<byte> data2)
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
