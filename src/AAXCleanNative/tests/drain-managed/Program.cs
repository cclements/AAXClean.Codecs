using AAXClean;
using AAXClean.Codecs;
using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using Mpeg4Lib.Chunks;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 4) throw new ArgumentException("usage: <library> <fixtures> <output> <v2|reject-v1>");
IntPtr native = NativeLibrary.Load(Path.GetFullPath(args[0]));
NativeLibrary.SetDllImportResolver(typeof(FfmpegAacDecoder).Assembly,
    (name, _, _) => name == "aaxcleannative" ? native : IntPtr.Zero);
Directory.CreateDirectory(args[2]);
if (args[3] == "reject-v1")
{
    bool rejected = false;
    try { using var unexpected = MakeDecoder("aac"); }
    catch (PlatformNotSupportedException e) when (e.Message.Contains("API v2")) { rejected = true; }
    if (!rejected) throw new InvalidOperationException("old native payload was not rejected before conversion");
    Console.WriteLine("PASS old native payload rejected before conversion");
    return;
}
if (args[3] != "v2") throw new ArgumentException("unknown mode");
List<object> results = [];
foreach (string codec in new[] { "aac", "eac3", "eac3-paired" })
{
    List<FrameEntry> input = ReadPackets(codec);
    List<WaveEntry> direct = [];
    using (var decoder = MakeDecoder(codec))
    {
        foreach (var entry in input) direct.AddRange(decoder.DecodeWave(entry));
        direct.AddRange(decoder.DecodeFlush());
        if (decoder.DecodeFlush().Any()) throw new InvalidOperationException("duplicate drain output");
    }
    using MemoryStream directPcm = new();
    long position = 0;
    foreach (var entry in direct)
    {
        if (entry.StartSample != position || entry.FrameData.Length != entry.SamplesInFrame * 4 || entry.Chunk is null)
            throw new InvalidOperationException("PCM position/size/source metadata mismatch");
        position += entry.SamplesInFrame;
        directPcm.Write(entry.FrameData.Span);
    }
    using AacToWave filter = new(MakeDecoder(codec));
    Sink sink = new(); filter.LinkTo(sink);
    foreach (var entry in input) await filter.AddInputAsync(entry);
    await filter.CompleteAsync();
    byte[] bytes = directPcm.ToArray();
    if (!sink.Flushed || !bytes.AsSpan().SequenceEqual(sink.Pcm.ToArray()))
        throw new InvalidOperationException("linked pipeline did not forward all decoded PCM before completion");
    string reference = Path.Combine(args[1], codec == "aac" ? "aac-reference.s16" : "eac3-reference.s16");
    if (bytes.Length != new FileInfo(reference).Length) throw new InvalidOperationException("independent sample count differs");
    File.WriteAllBytes(Path.Combine(args[2], codec + "-managed.s16"), bytes);
    results.Add(new { codec, packets = input.Count, outputs = direct.Count, samplesPerChannel = position,
        pipelineBytesEqual = true, continuousSourceTimeline = true });
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

FfmpegAacDecoder MakeDecoder(string codec)
{
    // A minimal real AudioSampleEntry; the native decoder receives the generated ASC.
    byte[] sampleEntry = new byte[36];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(sampleEntry, 36);
    System.Text.Encoding.ASCII.GetBytes(codec == "aac" ? "mp4a" : "ec-3").CopyTo(sampleEntry, 4);
    using MemoryStream sampleStream = new(sampleEntry);
    BoxHeader sampleHeader = new(sampleStream);
    AudioSampleEntry entry = new(sampleStream, sampleHeader, null);
    if (codec == "aac")
    {
        EsdsBox esds = EsdsBox.CreateEmpty(entry);
        esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = File.ReadAllBytes(Path.Combine(args[1], "aac.asc"));
    }
    else entry.Children.Add(new Dec3Box(new MemoryStream([0, 0, 0x20, 0x04, 0]), new BoxHeader(13, "dec3"), entry));
    return new FfmpegAacDecoder(entry, codec == "aac" ? 44100u : 48000u, WaveFormatEncoding.Pcm,
        codec == "aac" ? SampleRate.Hz_16000 : SampleRate.Hz_32000, stereo: true);
}

List<FrameEntry> ReadPackets(string codec)
{
    using BinaryReader reader = new(File.OpenRead(Path.Combine(args[1], codec + ".packets")));
    List<FrameEntry> entries = []; long start = 0;
    while (reader.BaseStream.Position < reader.BaseStream.Length)
    {
        int count = checked((int)reader.ReadUInt32()); byte[] data = reader.ReadBytes(count);
        if (count == 0 || data.Length != count) throw new InvalidDataException("packet fixture truncated");
        uint samples = 1024;
        if (codec != "aac")
        {
            samples = 0;
            for (int offset = 0; offset < data.Length;)
            {
                int size = 2 * ((((data[offset + 2] & 7) << 8) | data[offset + 3]) + 1);
                offset += size; samples += 1536;
            }
        }
        entries.Add(new FrameEntry {
            Chunk = new ChunkEntry { TrackId = 1, ChunkIndex = (uint)entries.Count, ChunkOffset = 0, FirstSample = start,
                ChunkSize = count, FrameSizes = [count], FrameDurations = [samples] },
            StartSample = start, SamplesInFrame = samples, FrameData = data, IsSyncSample = true,
        });
        start += samples;
    }
    return entries;
}

sealed class Sink : FrameFilterBase<WaveEntry>
{
    protected override int InputBufferSize => 2;
    public readonly MemoryStream Pcm = new();
    public bool Flushed;
    protected override Task HandleInputDataAsync(WaveEntry input) { Pcm.Write(input.FrameData.Span); return Task.CompletedTask; }
    protected override Task FlushAsync() { Flushed = true; return Task.CompletedTask; }
}
