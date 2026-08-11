using AAXClean.Codecs.FrameFilters.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpeg4Lib;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AAXClean.Codecs.Test;

[TestClass]
public class AacPresentationWindowTests
{
	private const int AacSamplesPerFrame = 1024;
	private const int AcceptedPcmSamples = 1501;

	[TestMethod]
	public async Task Single_output_presents_exactly_the_pcm_accepted_by_the_aac_encoder()
	{
		var format = new WaveFormat(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, stereo: false);
		byte[] pcm = CreatePcm(AcceptedPcmSamples);
		using var source = new AAXClean.Mp4File(new MemoryStream(CreateSourceMp4()));
		using var output = new MemoryStream();
		using var filter = new WaveToAacFilter(
			output,
			source,
			new ChapterQueue(SampleRate.Hz_16000, SampleRate.Hz_16000),
			format,
			bitrate: 24_000,
			quality: null);

		await filter.AddInputAsync(new WaveEntry
		{
			StartSample = 0,
			SamplesInFrame = AcceptedPcmSamples,
			FrameData = pcm,
		});
		await filter.CompleteAsync();

		using var converted = new AAXClean.Mp4File(new MemoryStream(output.ToArray()));
		long encodedMediaSamples = checked((long)converted.Moov.AudioTrack.Mdia.Mdhd.Duration);
		long paddedInputSamples = RoundUpToAacFrame(AcceptedPcmSamples);
		long observableEncoderDelay = encodedMediaSamples - paddedInputSamples;
		ElstBox.EditEntry edit = converted.Moov.AudioTrack.Edts!.Elst!.SingleEdit!.Value;

		Assert.IsGreaterThan(0L, observableEncoderDelay, "The fixture must exercise real encoder priming.");
		Assert.AreEqual(observableEncoderDelay, edit.MediaTime,
			"The presentation must begin after the encoder's observable priming samples.");
		Assert.AreEqual(AcceptedPcmSamples, converted.PresentedDurationSamples,
			"Encoder priming and zero-padded tail samples must not extend presentation duration.");
	}

	[TestMethod]
	public async Task Multipart_outputs_each_present_exactly_their_assigned_pcm_samples()
	{
		const int firstPartSamples = 1000;
		const int secondPartSamples = AcceptedPcmSamples - firstPartSamples;
		var format = new WaveFormat(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, stereo: false);
		using var source = new AAXClean.Mp4File(new MemoryStream(CreateSourceMp4()));
		ChapterInfo chapters = new();
		chapters.AddChapter("one", TimeSpan.FromTicks(firstPartSamples * 625L));
		chapters.AddChapter("two", TimeSpan.FromTicks(secondPartSamples * 625L));
		PresentationSampleMap presentationMap = new(
			inputTimescale: 16_000,
			outputSampleRate: 16_000,
			presentationStart: 0,
			presentedDuration: AcceptedPcmSamples);
		List<(string title, MemoryStream output)> parts = [];

		void NewPart(NewAacSplitCallback callback)
		{
			MemoryStream output = new();
			callback.OutputFile = output;
			parts.Add((callback.Chapter.Title, output));
		}

		using var filter = new WaveToAacMultipartFilter(
			chapters,
			source.Ftyp,
			source.Moov,
			format,
			new AacEncodingOptions { BitRate = 24_000, Stereo = false, SampleRate = SampleRate.Hz_16000 },
			NewPart,
			presentationMap.MapPresentationTime);
		await filter.AddInputAsync(new WaveEntry
		{
			Chunk = TestChunk,
			StartSample = 0,
			SamplesInFrame = AcceptedPcmSamples,
			FrameData = CreatePcm(AcceptedPcmSamples),
		});
		await filter.CompleteAsync();

		Assert.HasCount(2, parts);
		CollectionAssert.AreEqual(new[] { "one", "two" }, parts.Select(part => part.title).ToArray());
		long[] assignedSamples = [firstPartSamples, secondPartSamples];
		for (int partIndex = 0; partIndex < parts.Count; partIndex++)
		{
			using var converted = new AAXClean.Mp4File(new MemoryStream(parts[partIndex].output.ToArray()));
			Assert.AreEqual(assignedSamples[partIndex], converted.PresentedDurationSamples,
				$"Part {partIndex + 1} must not expose encoder priming or zero-padded tail samples.");
		}
	}

	[TestMethod]
	public async Task Entry_larger_than_one_aac_frame_routes_the_same_pcm_as_split_entries()
	{
		var format = new WaveFormat(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, stereo: false);
		byte[] pcm = CreatePcm(AcceptedPcmSamples);
		byte[] singleEntry = await EncodeAsync(format,
			new WaveEntry
			{
				StartSample = 0,
				SamplesInFrame = AcceptedPcmSamples,
				FrameData = pcm,
			});
		byte[] splitEntries = await EncodeAsync(format,
			new WaveEntry
			{
				StartSample = 0,
				SamplesInFrame = AacSamplesPerFrame,
				FrameData = pcm.AsMemory(0, AacSamplesPerFrame * format.BlockAlign),
			},
			new WaveEntry
			{
				StartSample = AacSamplesPerFrame,
				SamplesInFrame = AcceptedPcmSamples - AacSamplesPerFrame,
				FrameData = pcm.AsMemory(AacSamplesPerFrame * format.BlockAlign),
			});

		CollectionAssert.AreEqual(splitEntries, singleEntry,
			"Chunking PCM at the managed boundary must not change the AAC payload or presentation metadata.");
	}

	[TestMethod]
	public void Planar_pcm_is_rejected_before_native_encode()
	{
		var format = new WaveFormat(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, stereo: true);
		using var encoder = new FfmpegAacEncoder(format, bitRate: 24_000, quality: null);
		byte[] plane = CreatePcm(32);

		Assert.Throws<NotSupportedException>(() => encoder.EncodeWave(new WaveEntry
		{
			SamplesInFrame = 32,
			FrameData = plane,
			FrameData2 = plane,
		}).ToList());
		Assert.AreEqual(0L, encoder.AcceptedPcmSamples,
			"Rejected planar input must not be counted as accepted by the native encoder.");
	}

	private static async Task<byte[]> EncodeAsync(WaveFormat format, params WaveEntry[] entries)
	{
		using var source = new AAXClean.Mp4File(new MemoryStream(CreateSourceMp4()));
		using var output = new MemoryStream();
		using var filter = new WaveToAacFilter(
			output,
			source,
			new ChapterQueue(SampleRate.Hz_16000, SampleRate.Hz_16000),
			format,
			bitrate: 24_000,
			quality: null);

		foreach (WaveEntry entry in entries)
			await filter.AddInputAsync(entry);
		await filter.CompleteAsync();
		return output.ToArray();
	}

	private static byte[] CreatePcm(int samples)
	{
		byte[] pcm = new byte[checked(samples * sizeof(short))];
		for (int sample = 0; sample < samples; sample++)
		{
			short value = checked((short)((sample * 31 % 20_001) - 10_000));
			BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * sizeof(short), sizeof(short)), value);
		}
		return pcm;
	}

	private static long RoundUpToAacFrame(long samples)
		=> checked((samples + AacSamplesPerFrame - 1) / AacSamplesPerFrame * AacSamplesPerFrame);

	private static readonly ChunkEntry TestChunk = new()
	{
		TrackId = 1,
		ChunkIndex = 0,
		ChunkOffset = 0,
		FirstSample = 0,
		ChunkSize = 1,
		FrameSizes = [1],
		FrameDurations = [1],
	};

	private static byte[] CreateSourceMp4()
	{
		const uint timescale = 16_000;
		byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "), UInt32s(0));
		byte[] mdat = Box("mdat", [1, 0]);
		byte[] mvhd = Box("mvhd",
			UInt32s(0, 0, 0, timescale, 1024, 0x0001_0000),
			UInt16s(0x0100, 0), new byte[8], new byte[36], new byte[24], UInt32s(2));
		byte[] tkhd = Box("tkhd",
			UInt32s(0, 0, 0, 1, 0, 1024), new byte[8],
			UInt16s(0, 0, 0x0100, 0), new byte[36], UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, timescale, 1024, 0));
		byte[] hdlr = Box("hdlr", UInt32s(0, 0), Encoding.ASCII.GetBytes("soun"), new byte[12]);
		byte[] sampleEntry = Box("ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(1, 16, 0, 0, checked((ushort)timescale), 0), Box("dac4", [0]));
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, 1, 1024));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, 1, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, 1, 2));
		byte[] stco = Box("stco", UInt32s(0, 1, (uint)(ftyp.Length + 8)));
		byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] trak = Box("trak", tkhd, mdia);
		byte[] moov = Box("moov", mvhd, trak);
		return [.. ftyp, .. mdat, .. moov];
	}

	private static byte[] Box(string type, params byte[][] payloads)
	{
		using var box = new MemoryStream();
		WriteUInt32BE(box, checked((uint)(8 + payloads.Sum(payload => payload.Length))));
		box.Write(Encoding.ASCII.GetBytes(type));
		foreach (byte[] payload in payloads)
			box.Write(payload);
		return box.ToArray();
	}

	private static byte[] UInt32s(params uint[] values)
	{
		using var bytes = new MemoryStream();
		foreach (uint value in values)
			WriteUInt32BE(bytes, value);
		return bytes.ToArray();
	}

	private static byte[] UInt16s(params ushort[] values)
	{
		using var bytes = new MemoryStream();
		foreach (ushort value in values)
			WriteUInt16BE(bytes, value);
		return bytes.ToArray();
	}

	private static void WriteUInt32BE(Stream stream, uint value)
	{
		Span<byte> bytes = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}

	private static void WriteUInt16BE(Stream stream, ushort value)
	{
		Span<byte> bytes = [(byte)(value >> 8), (byte)value];
		stream.Write(bytes);
	}
}
