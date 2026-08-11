#nullable enable

using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.FrameFilters;
using Mpeg4Lib;
using Mpeg4Lib.Chunks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AAXClean.Codecs.Test;

[TestClass]
public class PresentationMultipartBoundaryTests
{
	[TestMethod]
	public void Decoder_coordinate_scaling_rounds_exact_ties_up()
	{
		Assert.AreEqual(1L, FfmpegAacDecoder.ScaleSamplePosition(1, inputTimescale: 2, outputSampleRate: 1));
	}

	[TestMethod]
	public void Presentation_map_uses_the_media_timescale_path_for_chapter_boundaries()
	{
		PresentationSampleMap map = new(
			inputTimescale: 2,
			outputSampleRate: 1,
			presentationStart: 0,
			presentedDuration: 2);

		// 250 ms is half of one input-media tick. Exact half-up mapping first reaches
		// media tick 1, then output sample 1; direct TimeSpan-to-output rounding would be 0.
		Assert.AreEqual(1L, map.MapPresentationTime(TimeSpan.FromMilliseconds(250)));
	}

	[TestMethod]
	public async Task Interleaved_pcm_is_split_at_the_exact_shared_chapter_boundary()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: false);
		var samples = Enumerable.Range(0, 1601).Select(value => (short)value).ToArray();
		var bytes = new byte[samples.Length * sizeof(short)];
		Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

		using RecordingWaveMultipartFilter filter = CreateFilter(format);
		await filter.AddInputAsync(WaveFrame(start: 0, samples: 1601, bytes));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new[] { "one", "two" }, filter.OpenedParts);
		// The first chapter maps through 44.1 kHz media tick 4413 to output sample
		// 801. Direct TimeSpan-to-8 kHz rounding would incorrectly split at 800.
		CollectionAssert.AreEqual(new uint[] { 801, 800 }, filter.Writes.Select(write => write.samples).ToArray());
		CollectionAssert.AreEqual(new long?[] { 0, 801 }, filter.Writes.Select(write => write.start).ToArray());
		Assert.AreEqual((short)0, BitConverter.ToInt16(filter.Writes[0].data));
		Assert.AreEqual((short)801, BitConverter.ToInt16(filter.Writes[1].data));
	}

	[TestMethod]
	public async Task Planar_pcm_splits_both_channels_at_the_same_exact_boundary()
	{
		var format = new WaveFormat(SampleRate.Hz_8000, WaveFormatEncoding.Pcm, stereo: true);
		var left = new byte[1601 * sizeof(short)];
		var right = new byte[1601 * sizeof(short)];
		for (short sample = 0; sample < 1601; sample++)
		{
			BitConverter.TryWriteBytes(left.AsSpan(sample * sizeof(short)), sample);
			BitConverter.TryWriteBytes(right.AsSpan(sample * sizeof(short)), (short)(sample + 2000));
		}

		using RecordingWaveMultipartFilter filter = CreateFilter(format);
		await filter.AddInputAsync(WaveFrame(start: 0, samples: 1601, left, right));
		await filter.CompleteAsync();

		CollectionAssert.AreEqual(new uint[] { 801, 800 }, filter.Writes.Select(write => write.samples).ToArray());
		Assert.HasCount(801 * sizeof(short), filter.Writes[0].data);
		Assert.HasCount(801 * sizeof(short), filter.Writes[0].data2);
		Assert.AreEqual((short)801, BitConverter.ToInt16(filter.Writes[1].data));
		Assert.AreEqual((short)2801, BitConverter.ToInt16(filter.Writes[1].data2));
	}

	private static RecordingWaveMultipartFilter CreateFilter(WaveFormat format)
	{
		ChapterInfo chapters = new();
		chapters.AddChapter("one", TimeSpan.FromTicks(1_000_567));
		chapters.AddChapter("two", TimeSpan.FromTicks(1_000_000));
		PresentationSampleMap map = new(44100, 8000, presentationStart: 0, presentedDuration: 8823);
		return new RecordingWaveMultipartFilter(chapters, format, map.MapPresentationTime);
	}

	private static readonly ChunkEntry Chunk = new()
	{
		TrackId = 1,
		ChunkIndex = 0,
		ChunkOffset = 0,
		FirstSample = 0,
		ChunkSize = 1,
		FrameSizes = [1],
		FrameDurations = [1],
	};

	private static WaveEntry WaveFrame(long start, uint samples, Memory<byte> data, Memory<byte> data2 = default)
		=> new()
		{
			Chunk = Chunk,
			StartSample = start,
			SamplesInFrame = samples,
			FrameData = data,
			FrameData2 = data2,
			Encoding = WaveFormatEncoding.Pcm,
			IsSyncSample = true,
		};

	private sealed class RecordingWaveMultipartFilter(
		ChapterInfo chapters,
		WaveFormat format,
		Func<TimeSpan, long> presentationTimeToSample)
		: WaveMultipartFilterBase<NewMP3SplitCallback>(chapters, format, presentationTimeToSample)
	{
		private string? currentPart;
		protected override int InputBufferSize => 10;
		public List<string> OpenedParts { get; } = [];
		public List<(string part, long? start, uint samples, byte[] data, byte[] data2)> Writes { get; } = [];

		protected override void CloseCurrentWriter() { }

		protected override void CreateNewWriter(NewMP3SplitCallback callback)
		{
			currentPart = callback.Chapter.Title;
			OpenedParts.Add(currentPart);
		}

		protected override void WriteFrameToFile(WaveEntry audioFrame, bool newChunk)
		{
			if (currentPart is not null)
				Writes.Add((currentPart, audioFrame.StartSample, audioFrame.SamplesInFrame,
					audioFrame.FrameData.ToArray(), audioFrame.FrameData2.ToArray()));
		}
	}
}
