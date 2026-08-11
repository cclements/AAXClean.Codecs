using AAXClean.Codecs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpeg4Lib;
using Mpeg4Lib.Boxes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AAXClean.Codecs.Test;

[TestClass]
public class PresentationEntryPointTests
{
	[TestMethod]
	public async Task Unsupported_edit_list_fails_before_single_mp3_output_is_mutated()
	{
		byte[] sourceBytes = CreateUnsupportedEditMp4();
		using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
		using var output = new MemoryStream();

		await Assert.ThrowsExactlyAsync<NotSupportedException>(
			async () => await source.ConvertToMp3Async(output));

		Assert.IsEmpty(output.ToArray());
	}

	[TestMethod]
	public void Unsupported_edit_list_is_validated_before_every_decode_entry_point()
	{
		ChapterInfo chapters = new();
		chapters.AddChapter("part", TimeSpan.FromSeconds(1));
		AacEncodingOptions aacOptions = new()
		{
			BitRate = 24_000,
			SampleRate = SampleRate.Hz_8000,
			Stereo = false,
		};

		AssertRejected(source => _ = source.DetectSilenceAsync(-30, TimeSpan.FromMilliseconds(1)));
		AssertRejected(source => _ = source.ConvertToMp4aAsync(new MemoryStream(), aacOptions));
		AssertRejected(source => _ = source.ConvertToMultiMp4aAsync(
			chapters, _ => Assert.Fail("Output callback must not run."), aacOptions));
		AssertRejected(source => _ = source.ConvertToMultiMp3Async(
			chapters, _ => Assert.Fail("Output callback must not run.")));

		static void AssertRejected(Action<AAXClean.Mp4File> start)
		{
			byte[] sourceBytes = CreateUnsupportedEditMp4();
			using var source = new AAXClean.Mp4File(new MemoryStream(sourceBytes));
			Assert.ThrowsExactly<NotSupportedException>(() => start(source));
		}
	}

	private static byte[] CreateUnsupportedEditMp4()
	{
		const uint movieTimescale = 1_000;
		const uint mediaTimescale = 8_000;
		const uint frameDelta = 8_000;
		byte[] samples = [1, 2, 3, 4, 5];
		uint sampleCount = (uint)samples.Length;
		uint mediaDuration = checked(sampleCount * frameDelta);
		ElstBox.EditEntry[] edits =
		[
			new(1_000, -1),
			new(1_000, 0),
		];

		byte[] ftyp = Box("ftyp", Encoding.ASCII.GetBytes("M4A "), UInt32s(0));
		byte[] mediaPayload = samples.SelectMany(sample => new byte[] { sample, 0 }).ToArray();
		byte[] mdat = Box("mdat", mediaPayload);
		byte[] mvhd = Box("mvhd",
			UInt32s(0, 0, 0, movieTimescale, 2_000, 0x0001_0000),
			UInt16s(0x0100, 0), new byte[8], new byte[36], new byte[24], UInt32s(2));
		byte[] tkhd = Box("tkhd",
			UInt32s(0, 0, 0, 1, 0, 2_000), new byte[8],
			UInt16s(0, 0, 0x0100, 0), new byte[36], UInt32s(0, 0));
		byte[] mdhd = Box("mdhd", UInt32s(0, 0, 0, mediaTimescale, mediaDuration, 0));
		byte[] hdlr = Box("hdlr", UInt32s(0, 0), Encoding.ASCII.GetBytes("soun"), new byte[12]);
		byte[] sampleEntry = Box("ac-4",
			new byte[6], UInt16s(1), new byte[8],
			UInt16s(2, 16, 0, 0, checked((ushort)mediaTimescale), 0), Box("dac4", [0]));
		byte[] stsd = Box("stsd", UInt32s(0, 1), sampleEntry);
		byte[] stts = Box("stts", UInt32s(0, 1, sampleCount, frameDelta));
		byte[] stsc = Box("stsc", UInt32s(0, 1, 1, sampleCount, 1));
		byte[] stsz = Box("stsz", UInt32s(0, 0, sampleCount),
			UInt32s(Enumerable.Repeat(2u, samples.Length).ToArray()));
		byte[] stco = Box("stco", UInt32s(0, 1, (uint)(ftyp.Length + 8)));
		byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
		byte[] minf = Box("minf", stbl);
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] trak = Box("trak", tkhd, EditBox(edits), mdia);
		byte[] moov = Box("moov", mvhd, trak);

		return [.. ftyp, .. mdat, .. moov];
	}

	private static byte[] EditBox(IReadOnlyList<ElstBox.EditEntry> edits)
	{
		using var payload = new MemoryStream();
		WriteUInt32BE(payload, 0);
		WriteUInt32BE(payload, (uint)edits.Count);
		foreach (ElstBox.EditEntry edit in edits)
		{
			WriteUInt32BE(payload, checked((uint)edit.SegmentDuration));
			WriteUInt32BE(payload, unchecked((uint)edit.MediaTime));
			WriteUInt16BE(payload, unchecked((ushort)edit.MediaRateInteger));
			WriteUInt16BE(payload, unchecked((ushort)edit.MediaRateFraction));
		}
		return Box("edts", Box("elst", payload.ToArray()));
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
