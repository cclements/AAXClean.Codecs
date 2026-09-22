using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.Codecs.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpeg4Lib;
using Mpeg4Lib.Chunks;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace AAXClean.Codecs.Test;

// Produces synthetic files for tests/verify-encoder-signals.py. Metadata assertions
// run here; independent decode/alignment is a separate, explicit verification step.
[TestClass]
public class EncoderSignalTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1, 16000, false)]
    [DataRow(128, 16000, false)]
    [DataRow(1024, 16000, false)]
    [DataRow(1501, 16000, false)]
    [DataRow(16001, 16000, false)]
    [DataRow(128, 44100, true)]
    [DataRow(1024, 44100, true)]
    [DataRow(1501, 44100, true)]
    [DataRow(16001, 44100, true)]
    public async Task Single_signal_has_reported_delay_and_exact_presentation(int count, int rate, bool stereo)
    {
        var format = new WaveFormat((SampleRate)rate, WaveFormatEncoding.Pcm, stereo);
        byte[] pcm = Signal(count, format.Channels);
        using var source = new Mp4File(new MemoryStream(AacPresentationWindowTests.CreateSourceMp4()));
        using var output = new MemoryStream();
        using var filter = new WaveToAacFilter(output, source,
            new ChapterQueue((SampleRate)rate, (SampleRate)rate), format, 64000 * format.Channels, null);
        await filter.AddInputAsync(new WaveEntry { StartSample = 0, SamplesInFrame = (uint)count, FrameData = pcm });
        await filter.CompleteAsync();
        VerifyAndSave($"single-{count}-{rate}-{format.Channels}", output.ToArray(), pcm, format);
    }

    [TestMethod]
    public async Task Each_chapter_signal_is_a_standalone_presentation()
    {
        var format = new WaveFormat(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, false);
        int[] lengths = [1000, 3073, 2928];
        byte[] pcm = Signal(lengths.Sum(), 1);
        ChapterInfo chapters = new();
        foreach (int count in lengths) chapters.AddChapter($"part-{chapters.Count}", TimeSpan.FromTicks(count * 625L));
        using var source = new Mp4File(new MemoryStream(AacPresentationWindowTests.CreateSourceMp4()));
        List<MemoryStream> parts = [];
        using var filter = new WaveToAacMultipartFilter(chapters, source.Ftyp, source.Moov, format,
            new AacEncodingOptions { BitRate = 64000, Stereo = false, SampleRate = SampleRate.Hz_16000 },
            callback => { var output = new MemoryStream(); callback.OutputFile = output; parts.Add(output); },
            time => time.Ticks / 625);
        // Different input chunks and chapter boundaries must route every sample exactly once.
        int offset = 0;
        foreach (int count in new[] { 777, 2048, 4176 })
        {
            await filter.AddInputAsync(new WaveEntry
            {
                Chunk = new ChunkEntry { TrackId = 1, ChunkIndex = 0, ChunkOffset = 0, FirstSample = 0,
                    ChunkSize = 1, FrameSizes = [1], FrameDurations = [1] },
                StartSample = offset, SamplesInFrame = (uint)count,
                FrameData = pcm.AsMemory(offset * 2, count * 2)
            });
            offset += count;
        }
        await filter.CompleteAsync();
        Assert.HasCount(lengths.Length, parts);
        offset = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            VerifyAndSave($"chapter-{i}", parts[i].ToArray(), pcm.AsSpan(offset * 2, lengths[i] * 2).ToArray(), format);
            offset += lengths[i];
        }
    }

    private void VerifyAndSave(string name, byte[] mp4, byte[] pcm, WaveFormat format)
    {
        using var encoded = new Mp4File(new MemoryStream(mp4));
        using var native = new NativeAacEncode(format, 64000 * format.Channels, 0);
        long samples = pcm.Length / format.BlockAlign;
        Assert.AreEqual((long)native.InitialPadding, encoded.PresentationStartSample);
        Assert.AreEqual(samples, encoded.PresentedDurationSamples);
        Assert.IsGreaterThan(0, new ChunkEntryList(encoded.Moov.AudioTrack).Count(),
            "Even a short input whose packets all arrive during flush needs a sample-to-chunk entry.");
        Assert.AreEqual((uint)format.SampleRate, encoded.Moov.AudioTrack.Mdia.Mdhd.Timescale);
        Assert.IsGreaterThanOrEqualTo(samples + native.InitialPadding, (long)encoded.Moov.AudioTrack.Mdia.Mdhd.Duration);
        string root = Environment.GetEnvironmentVariable("AAXCLEAN_SIGNAL_OUTPUT")
            ?? Path.Combine(TestContext.TestRunDirectory!, "encoder-signals");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, name + ".m4a"), mp4);
        File.WriteAllBytes(Path.Combine(root, name + ".s16"), pcm);
        File.WriteAllText(Path.Combine(root, name + ".json"), JsonSerializer.Serialize(new
        {
            name, samples, channels = format.Channels, rate = format.SampleRate,
            delay = native.InitialPadding, media_samples = encoded.Moov.AudioTrack.Mdia.Mdhd.Duration,
            mp4_sha256 = Convert.ToHexString(SHA256.HashData(mp4)).ToLowerInvariant(),
            pcm_sha256 = Convert.ToHexString(SHA256.HashData(pcm)).ToLowerInvariant()
        }));
        TestContext.AddResultFile(Path.Combine(root, name + ".json"));
    }

    private static byte[] Signal(int count, int channels)
    {
        byte[] pcm = new byte[count * channels * 2];
        for (int sample = 0; sample < count; sample++)
            for (int channel = 0; channel < channels; channel++)
            {
                double t = sample + 7 + channel * 11;
                short value = (short)(12000 * Math.Sin(t * (.2 + channel * .073))
                    + 6000 * Math.Sin(t * .41 + t * t * .000017));
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((sample * channels + channel) * 2, 2), value);
            }
        return pcm;
    }
}
