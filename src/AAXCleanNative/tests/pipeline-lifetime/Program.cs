using AAXClean;
using AAXClean.Codecs;
using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.FrameFilters;
using Mpeg4Lib;
using Mpeg4Lib.Chunks;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string Probe = "native-safety";
    [DllImport(Probe)] private static extern void Safety_Reset();
    [DllImport(Probe)] private static extern int Safety_EncoderCloses();
    [DllImport(Probe)] private static extern int Safety_LiveWrappers();
    [DllImport(Probe)] private static extern int Safety_LiveResources();
    private static readonly WaveFormat Format = new(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, false);

    private static async Task Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("usage: PipelineLifetime <probe dylib> <synthetic source m4a> <case>");
        IntPtr native = NativeLibrary.Load(Path.GetFullPath(args[0]));
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) => name == Probe ? native : IntPtr.Zero);
        NativeLibrary.SetDllImportResolver(typeof(WaveToAacFilter).Assembly, (name, _, _) => name == "aaxcleannative" ? native : IntPtr.Zero);
        using var source = new Mp4File(File.OpenRead(args[1]));
        Safety_Reset();
        // No finalizer may make an abandoned encoder appear promptly disposed.
        Require(GC.TryStartNoGCRegion(64 * 1024 * 1024), "NoGCRegion unavailable");
        try { await Run(source, args[2]); }
        finally { GC.EndNoGCRegion(); }
        Console.WriteLine($"PASS {args[2]}: exact closes, zero native resources before GC and repeated Dispose");
    }

    private static async Task Run(Mp4File source, string name)
    {
        bool multipart = name.StartsWith("multipart-", StringComparison.Ordinal);
        bool constructionFailure = name.EndsWith("construction-failure", StringComparison.Ordinal);
        bool writeFailure = name.EndsWith("write-failure", StringComparison.Ordinal);
        bool flushFailure = name.EndsWith("flush-failure", StringComparison.Ordinal);
        Require(name is "single-complete" or "multipart-complete"
            or "single-construction-failure" or "multipart-construction-failure"
            or "single-write-failure" or "multipart-write-failure"
            or "single-flush-failure" or "multipart-flush-failure", "unknown case");
        bool fails = constructionFailure || writeFailure || flushFailure;
        int count = writeFailure ? 16000 : 128;
        List<FaultStream> outputs = [];
        var expected = new IOException("injected output failure");
        FrameFinalBase<WaveEntry>? filter = null;
        Exception? observed = null;
        int headerSize;
        using (var header = new MemoryStream())
        {
            source.Ftyp.Save(header);
            headerSize = checked((int)header.Length + 16);
        }
        FaultStream CreateOutput()
        {
            var stream = new FaultStream(expected, constructionFailure ? 0 : writeFailure || flushFailure ? headerSize : long.MaxValue);
            outputs.Add(stream);
            return stream;
        }
        try
        {
            if (multipart)
            {
                ChapterInfo chapters = new();
                int parts = fails ? 1 : 3;
                for (int i = 0; i < parts; i++) chapters.AddChapter($"part-{i}", TimeSpan.FromTicks(count * 625L));
                filter = new WaveToAacMultipartFilter(chapters, source.Ftyp, source.Moov, Format,
                    new AacEncodingOptions { BitRate = 64000, Stereo = false, SampleRate = SampleRate.Hz_16000 },
                    callback =>
                    {
                        // The preceding chapter must close before a new output is requested.
                        AssertBalance(outputs.Count, $"before callback {outputs.Count}");
                        callback.OutputFile = CreateOutput();
                    }, time => time.Ticks / 625);
                count *= parts;
            }
            else filter = new WaveToAacFilter(CreateOutput(), source,
                new ChapterQueue(SampleRate.Hz_16000, SampleRate.Hz_16000), Format, 64000, null);

            await filter.AddInputAsync(new WaveEntry
            {
                Chunk = new ChunkEntry { TrackId = 1, ChunkIndex = 0, ChunkOffset = 0, FirstSample = 0,
                    ChunkSize = 1, FrameSizes = [1], FrameDurations = [1] },
                StartSample = 0, SamplesInFrame = (uint)count, FrameData = new byte[count * 2]
            });
            await filter.CompleteAsync();
        }
        catch (Exception error) { observed = error; }

        // Inspect before filter Dispose and before any collector/finalizer runs.
        AssertBalance(multipart && !fails ? 3 : 1, "after completion/construction");
        if (fails) Require(Contains(observed, expected), $"original output failure missing: {observed}");
        else Require(observed is null, $"unexpected failure: {observed}");
        if (writeFailure || flushFailure)
        {
            string stage = multipart
                ? flushFailure ? "CloseCurrentWriter" : "WriteFrameToFile"
                : flushFailure ? "FlushAsync" : "PerformFilteringAsync";
            Require(expected.StackTrace?.Contains(stage, StringComparison.Ordinal) is true,
                $"injected failure did not reach {stage}: {expected.StackTrace}");
        }
        // Recover the test stream so disposal itself can be checked independently.
        foreach (var stream in outputs) stream.FailAfter = long.MaxValue;
        filter?.Dispose();
        filter?.Dispose();
        AssertBalance(multipart && !fails ? 3 : 1, "after repeated dispose");
        foreach (var stream in outputs) stream.Dispose();
    }

    private static bool Contains(Exception? actual, Exception expected)
        => ReferenceEquals(actual, expected)
        || actual is AggregateException aggregate && aggregate.InnerExceptions.Any(e => Contains(e, expected))
        || actual?.InnerException is not null && Contains(actual.InnerException, expected);

    private static void AssertBalance(int closes, string phase)
    {
        int actual = Safety_EncoderCloses(), wrappers = Safety_LiveWrappers(), resources = Safety_LiveResources();
        Console.WriteLine($"COUNTERS {phase}: closes={actual}/{closes} wrappers={wrappers} resources={resources}");
        Require(actual == closes && wrappers == 0 && resources == 0, $"native resource imbalance at {phase}");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FaultStream(IOException failure, long failAfter) : MemoryStream
    {
        public long FailAfter = failAfter;
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > FailAfter) throw failure;
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > FailAfter) throw failure;
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Position + 1 > FailAfter) throw failure;
            base.WriteByte(value);
        }
    }
}
