using AAXClean;
using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.Codecs.Interop;
using Mpeg4Lib.Boxes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string ProbeLibrary = "native-safety";
    [DllImport(ProbeLibrary)] private static extern void Safety_SetFailure(int failure);
    [DllImport(ProbeLibrary)] private static extern void Safety_Reset();
    [DllImport(ProbeLibrary)] private static extern int Safety_DecoderCloses();
    [DllImport(ProbeLibrary)] private static extern int Safety_EncoderCloses();
    [DllImport(ProbeLibrary)] private static extern int Safety_LiveWrappers();
    [DllImport(ProbeLibrary)] private static extern int Safety_LiveResources();
    [DllImport(ProbeLibrary)] private static extern int Safety_SizeOfOptions(int kind);

    private static readonly WaveFormat Format = new(SampleRate.Hz_44100, WaveFormatEncoding.Pcm, true);
    private static readonly TestBox Parent = new();
    private static readonly EsdsBox Esds = CreateEsds();
    private static readonly Dec3Box Dec3 = new(new MemoryStream([0, 0, 0x20, 0x04, 0]), new BoxHeader(13, "dec3"), null);
    private static readonly Dac4Box Dac4 = new(new MemoryStream([0]), new BoxHeader(9, "dac4"), null);
    private static int tests;

    private static EsdsBox CreateEsds()
    {
        var box = EsdsBox.CreateEmpty(Parent);
        box.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = [0x12, 0x10];
        return box;
    }
    private static IDisposable Create(int kind) => kind switch
    {
        0 => new NativeAacDecode(Esds, Format),
        1 => new NativeEc3Decode(Dec3, Format),
        2 => new NativeAc4Decode(Dac4, Format),
        3 => new NativeAacEncode(Format, 128000, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Pass(string name) { tests++; Console.WriteLine($"PASS {name}"); }
    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(int kind) => new(Create(kind));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FailedOpen(int kind, int point)
    {
        Safety_SetFailure(point);
        bool threw = false;
        try { using var unexpected = Create(kind); }
        catch (Exception error) when (error.Message.StartsWith("Error opening ", StringComparison.Ordinal)) { threw = true; }
        Require(threw, $"constructor must reject native failure kind={kind} point={point}");
    }
    private static void Main(string[] args)
    {
        Require(args.Length is 1 or 2, "usage: NativeHandleSafety <absolute dylib> [smoke]");
        IntPtr native = NativeLibrary.Load(Path.GetFullPath(args[0]));
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
            (name, _, _) => name is "aaxcleannative" or ProbeLibrary ? native : IntPtr.Zero);
        if (args.Length == 2)
        {
            Require(args[1] == "smoke", "unknown mode");
            RuntimeSmoke();
            return;
        }

        Type decoder = typeof(NativeDecode);
        Type output = decoder.GetNestedType("OutputOptions", BindingFlags.NonPublic)!;
        Type aacOptions = typeof(NativeAacDecode).GetNestedType("AacDecoderOptions", BindingFlags.NonPublic)!;
        Type encoderOptions = typeof(NativeAacEncode).GetNestedType("AacEncoderOptions", BindingFlags.NonPublic)!;
        Require(Marshal.SizeOf(output) == Safety_SizeOfOptions(0), "OutputOptions native/managed size");
        Require(Marshal.SizeOf(aacOptions) == Safety_SizeOfOptions(1), "AAC options native/managed size");
        Require(Marshal.OffsetOf(aacOptions, "ASC").ToInt32() == 16, "AAC pointer offset");
        Require(Marshal.SizeOf(encoderOptions) == Safety_SizeOfOptions(2), "encoder options native/managed size");
        Require(Marshal.OffsetOf(encoderOptions, "sample_fmt").ToInt32() == 20, "encoder sample format offset");
        Pass("native-managed ABI sizes and offsets");

        for (int kind = 0; kind < 4; kind++)
        {
            for (int point = 1; point <= 7; point++)
            {
                if (kind != 0 && point == 4) continue;
                Collect();
                Safety_Reset();
                FailedOpen(kind, point);
                Collect();
                Require(Safety_DecoderCloses() == 0 && Safety_EncoderCloses() == 0,
                    $"failed native open must never become a closeable managed handle kind={kind} point={point}");
                Require(Safety_LiveWrappers() == 0 && Safety_LiveResources() == 0, "failed open resource balance");
                Pass($"failed-open/finalization kind={kind} point={point}");
            }
            Safety_Reset();
            var instance = Create(kind);
            instance.Dispose();
            instance.Dispose();
            Collect();
            Require((kind == 3 ? Safety_EncoderCloses() : Safety_DecoderCloses()) == 1,
                "repeated Dispose must release exactly once");
            Require(Safety_LiveWrappers() == 0 && Safety_LiveResources() == 0, "dispose resource balance");
            Pass($"repeated-dispose kind={kind}");

            Safety_Reset();
            WeakReference abandoned = Abandon(kind);
            Collect();
            Require(!abandoned.IsAlive, "abandoned wrapper must be collectible");
            Require((kind == 3 ? Safety_EncoderCloses() : Safety_DecoderCloses()) == 1,
                "SafeHandle finalization must release exactly once");
            Require(Safety_LiveWrappers() == 0 && Safety_LiveResources() == 0, "finalization resource balance");
            Pass($"abandoned-finalization kind={kind}");
        }
        Require(tests == 34, $"expected 34 cases, observed {tests}");
        Console.WriteLine($"PASS managed native safety: {tests}/34 cases");
        // Keep the native library loaded until process teardown: SafeHandle release
        // code and the runtime's import cache may outlive this method.
    }

    private static unsafe void RuntimeSmoke()
    {
        using var encoder = new NativeAacEncode(Format, 128000, 0);
        Esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = encoder.GetAudioSpecificConfig();
        using var decoder = new NativeAacDecode(Esds, Format);
        using var ec3 = new NativeEc3Decode(Dec3, Format);
        using var ac4 = new NativeAc4Decode(Dac4, Format);
        short[] input = new short[1024 * 2];
        int packets = 0, samples = 0;
        for (int frame = 0; frame < 12; frame++)
        {
            fixed (short* pcm = input)
                Require(encoder.EncodeFrame((byte*)pcm, null, 1024) >= 0, "encode synthetic PCM");
            int size = encoder.ReceiveEncodedFrame(null, 0);
            if (size == 0) continue;
            Require(size > 0, "encoded packet size");
            byte[] packet = new byte[size];
            fixed (byte* data = packet)
            {
                Require(encoder.ReceiveEncodedFrame(data, size) == 0, "receive encoded packet");
                Require(decoder.DecodeFrame(data, size) >= 0, "decode managed packet");
            }
            int required = decoder.ReceiveDecodedFrame(null, null, 0);
            Require(required > 0, "decoded frame buffer size");
            short[] output = new short[required * 2];
            fixed (short* pcm = output)
            {
                int received = decoder.ReceiveDecodedFrame((byte*)pcm, null, required);
                Require(received >= 0 && received <= required, "decoded sample bound");
                samples += received;
            }
            packets++;
        }
        Require(packets > 0 && samples > 0, "real AAC round trip produces PCM");
        Console.WriteLine($"PASS production-dylib smoke: AAC packets={packets}, decoded samples/channel={samples}; EC3/AC4 open and dispose");
    }
    private sealed class TestBox() : Box(new BoxHeader(8, "moov"), null)
    {
        protected override void Render(Stream file) { }
    }
}
