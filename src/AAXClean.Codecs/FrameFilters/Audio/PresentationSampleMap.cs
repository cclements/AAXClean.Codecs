using Mpeg4Lib.Boxes;
using System;

namespace AAXClean.Codecs.FrameFilters.Audio;

/// <summary>
/// Maps presentation times into decoded-sample coordinates through the source media
/// timescale. Keeping this single map at both the crop and multipart seams prevents
/// independently rounded chapter boundaries from gaining or losing a sample.
/// </summary>
internal sealed class PresentationSampleMap
{
	private readonly uint inputTimescale;
	private readonly uint outputSampleRate;
	private readonly long presentationStart;
	private readonly long presentationEnd;
	private readonly long outputOrigin;

	public PresentationSampleMap(
		uint inputTimescale,
		uint outputSampleRate,
		long presentationStart,
		long presentedDuration)
	{
		ArgumentOutOfRangeException.ThrowIfZero(inputTimescale);
		ArgumentOutOfRangeException.ThrowIfZero(outputSampleRate);
		ArgumentOutOfRangeException.ThrowIfNegative(presentationStart);
		ArgumentOutOfRangeException.ThrowIfNegative(presentedDuration);

		this.inputTimescale = inputTimescale;
		this.outputSampleRate = outputSampleRate;
		this.presentationStart = presentationStart;
		presentationEnd = checked(presentationStart + presentedDuration);
		outputOrigin = ScaleMediaPosition(presentationStart);
	}

	/// <summary>Map a presentation-relative time to a presentation-relative decoded sample.</summary>
	public long MapPresentationTime(TimeSpan presentationTime)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(presentationTime, TimeSpan.Zero);
		long mediaOffset = checked((long)ElstBox.ScaleDuration(
			(ulong)presentationTime.Ticks,
			(uint)TimeSpan.TicksPerSecond,
			inputTimescale));
		long absoluteMediaPosition = checked(presentationStart + mediaOffset);
		return checked(ScaleMediaPosition(absoluteMediaPosition) - outputOrigin);
	}

	public PresentationWindowFilter CreateWindowFilter(WaveFormat outputFormat, TimeSpan start, TimeSpan end)
	{
		ArgumentNullException.ThrowIfNull(outputFormat);
		long windowStart = checked(outputOrigin + MapPresentationTime(start));
		long windowEnd = end == TimeSpan.MaxValue
			? ScaleMediaPosition(presentationEnd)
			: checked(outputOrigin + MapPresentationTime(end));
		return new PresentationWindowFilter(outputFormat, windowStart, windowEnd, outputOrigin);
	}

	private long ScaleMediaPosition(long mediaPosition)
		=> checked((long)ElstBox.ScaleDuration(
			checked((ulong)mediaPosition),
			inputTimescale,
			outputSampleRate));
}
