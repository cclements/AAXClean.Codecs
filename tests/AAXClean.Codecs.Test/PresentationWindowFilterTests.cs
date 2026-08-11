using AAXClean.Codecs.FrameFilters.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpeg4Lib;
using System;
using System.IO;

namespace AAXClean.Codecs.Test;

[TestClass]
public class PresentationWindowFilterTests
{
	[TestMethod]
	public void Crops_decoded_pcm_to_the_exact_window_and_rebases_to_presentation_time()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: false);
		var inputBytes = new byte[20 * format.BlockAlign];
		for (short sample = 0; sample < 20; sample++)
			BitConverter.TryWriteBytes(inputBytes.AsSpan(sample * format.BlockAlign), sample);
		var input = new WaveEntry
		{
			StartSample = 90,
			SamplesInFrame = 20,
			FrameData = inputBytes,
		};
		var filter = new PresentationWindowFilter(format, windowStart: 100, windowEnd: 105, presentationOrigin: 50);

		var output = filter.PerformFiltering(input);

		Assert.AreEqual(50L, output.StartSample);
		Assert.AreEqual(5u, output.SamplesInFrame);
		Assert.AreEqual(5 * format.BlockAlign, output.FrameData.Length);
		Assert.AreEqual((short)10, BitConverter.ToInt16(output.FrameData.Span));
		Assert.AreEqual((short)14, BitConverter.ToInt16(output.FrameData.Span[^format.BlockAlign..]));
	}

	[TestMethod]
	public void Drops_pcm_that_is_entirely_outside_the_window()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: false);
		var input = new WaveEntry
		{
			StartSample = 0,
			SamplesInFrame = 10,
			FrameData = new byte[10 * format.BlockAlign],
		};
		var filter = new PresentationWindowFilter(format, windowStart: 10, windowEnd: 20, presentationOrigin: 0);

		var output = filter.PerformFiltering(input);

		Assert.AreEqual(0u, output.SamplesInFrame);
		Assert.IsTrue(output.FrameData.IsEmpty);
	}

	[TestMethod]
	public void Rejects_nonempty_pcm_without_a_source_coordinate()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: false);
		var input = new WaveEntry
		{
			SamplesInFrame = 1,
			FrameData = new byte[format.BlockAlign],
		};
		var filter = new PresentationWindowFilter(format, windowStart: 0, windowEnd: 1, presentationOrigin: 0);

		Assert.Throws<InvalidDataException>(() => filter.PerformFiltering(input));
	}

	[TestMethod]
	public void Crops_both_planar_stereo_channels_at_the_same_sample_boundary()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: true);
		var left = new byte[10 * sizeof(short)];
		var right = new byte[10 * sizeof(short)];
		for (short sample = 0; sample < 10; sample++)
		{
			BitConverter.TryWriteBytes(left.AsSpan(sample * sizeof(short)), sample);
			BitConverter.TryWriteBytes(right.AsSpan(sample * sizeof(short)), (short)(sample + 100));
		}
		var input = new WaveEntry
		{
			StartSample = 0,
			SamplesInFrame = 10,
			FrameData = left,
			FrameData2 = right,
		};
		var filter = new PresentationWindowFilter(format, windowStart: 2, windowEnd: 5, presentationOrigin: 0);

		var output = filter.PerformFiltering(input);

		Assert.AreEqual(3u, output.SamplesInFrame);
		Assert.AreEqual(3 * sizeof(short), output.FrameData.Length);
		Assert.AreEqual(3 * sizeof(short), output.FrameData2.Length);
		Assert.AreEqual((short)2, BitConverter.ToInt16(output.FrameData.Span));
		Assert.AreEqual((short)102, BitConverter.ToInt16(output.FrameData2.Span));
	}

	[TestMethod]
	public void Rescales_decoder_coordinates_between_media_and_output_timescales()
	{
		Assert.AreEqual(16_000L, FfmpegAacDecoder.ScaleSamplePosition(44_100, 44_100, 16_000));
		Assert.AreEqual(8_000L, FfmpegAacDecoder.ScaleSamplePosition(22_050, 44_100, 16_000));
	}
}
