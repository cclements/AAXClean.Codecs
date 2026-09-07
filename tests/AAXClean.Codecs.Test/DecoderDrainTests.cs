#nullable enable
using AAXClean.Codecs.FrameFilters.Audio;
using AAXClean.Codecs.Interop;
using AAXClean.FrameFilters;
using Mpeg4Lib.Chunks;
using System.IO;

namespace AAXClean.Codecs.Test;

[TestClass]
public class DecoderDrainTests
{
    private static readonly WaveFormat Pcm = new(SampleRate.Hz_16000, WaveFormatEncoding.Pcm, true);

    [TestMethod]
    public void BufferedPackets_ProduceMultipleFramesAndDelayedTailOnContinuousTimeline()
    {
        FakeNative native = new();
        native.OnSubmit = count => { if (count == 2) native.AddPcm(3, 10); };
        native.OnFinish = () => { native.AddPcm(5, 20); native.End(); };
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.IsEmpty(decoder.DecodeWave(Input(0, 4)).ToArray());
        WaveEntry[] second = decoder.DecodeWave(Input(4, 4)).ToArray();
        WaveEntry[] tail = decoder.DecodeFlush().ToArray();
        WaveEntry[] all = [.. second, .. tail];
        CollectionAssert.AreEqual(new long?[] { 0, 3, 4 }, all.Select(e => e.StartSample).ToArray());
        CollectionAssert.AreEqual(new uint[] { 3, 1, 4 }, all.Select(e => e.SamplesInFrame).ToArray());
        CollectionAssert.AreEqual(new byte[] { 10, 20, 20 }, all.Select(e => e.FrameData.Span[0]).ToArray());
        Assert.AreEqual(1, native.FinishCalls);
    }

    [TestMethod]
    public void UnacceptedPacket_IsRetriedUnchangedWithoutDuplicatingMetadata()
    {
        FakeNative native = new();
        native.SubmitResults.Enqueue(NativeDecode.Accepted);
        native.SubmitResults.Enqueue(NativeDecode.ReceiveFirst);
        native.SubmitResults.Enqueue(NativeDecode.Accepted);
        native.OnSubmit = count => { if (count == 2) native.AddPcm(4); if (count == 3) native.AddPcm(4); };
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.IsEmpty(decoder.DecodeWave(Input(0, 4, 1)).ToArray());
        WaveEntry[] frames = decoder.DecodeWave(Input(4, 4, 2)).ToArray();
        CollectionAssert.AreEqual(new byte[] { 1, 2, 2 }, native.Submitted.Select(p => p[0]).ToArray());
        CollectionAssert.AreEqual(new long?[] { 0, 4 }, frames.Select(e => e.StartSample).ToArray());
    }

    [TestMethod]
    public void EofBackpressure_ReceivesThenRetriesEofAndDrainsTail()
    {
        FakeNative native = new();
        native.FinishResults.Enqueue(NativeDecode.ReceiveFirst);
        native.FinishResults.Enqueue(NativeDecode.Accepted);
        native.OnFinish = () => { native.AddPcm(2); if (native.FinishCalls == 2) native.End(); };
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        _ = decoder.DecodeWave(Input(0, 4)).ToArray();
        Assert.AreEqual(4L, decoder.DecodeFlush().Sum(e => (long)e.SamplesInFrame));
        Assert.AreEqual(2, native.FinishCalls);
        Assert.IsEmpty(decoder.DecodeFlush().ToArray());
        Assert.AreEqual(2, native.FinishCalls);
        Assert.ThrowsExactly<InvalidOperationException>(() => decoder.DecodeWave(Input(4, 1)).ToArray());
    }

    [TestMethod]
    public void PacketBackpressureWithoutReceiveProgress_FailsInsteadOfDroppingPacket()
    {
        FakeNative native = new(); native.SubmitResults.Enqueue(NativeDecode.ReceiveFirst);
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.DecodeWave(Input(0, 4)).ToArray());
        Assert.HasCount(1, native.Submitted);
    }

    [TestMethod]
    public void EofBackpressureWithoutReceiveProgress_FailsPromptly()
    {
        FakeNative native = new(); native.FinishResults.Enqueue(NativeDecode.ReceiveFirst);
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.DecodeFlush().ToArray());
        Assert.AreEqual(1, native.FinishCalls);
    }

    [TestMethod]
    public void NeedInputAfterAcceptedEof_IsNotSuccessfulCompletion()
    {
        using FfmpegAacDecoder decoder = new(new FakeNative(), Pcm, 16000);
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.DecodeFlush().ToArray());
    }

    [TestMethod]
    public void EmptyCompressedPacket_IsNotImplicitEof()
    {
        FakeNative native = new();
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        FrameEntry empty = new() { SamplesInFrame = 0, FrameData = Memory<byte>.Empty };
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.DecodeWave(empty).ToArray());
        Assert.AreEqual(0, native.FinishCalls); Assert.IsEmpty(native.Submitted);
    }

    [TestMethod]
    public void RepeatedEmptyDecodedFrames_AreBounded()
    {
        FakeNative native = new() { EndlessEmpty = true };
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.ThrowsExactly<InvalidDataException>(() => decoder.DecodeWave(Input(0, 1)).ToArray());
        Assert.AreEqual(1025, native.ReceiveCalls);
    }

    [TestMethod]
    public void CancellationBeforeSubmit_DoesNotAcceptInput()
    {
        FakeNative native = new();
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        Assert.ThrowsExactly<OperationCanceledException>(() => decoder.DecodeWave(Input(0, 1), new(true)).ToArray());
        Assert.IsEmpty(native.Submitted);
    }

    [TestMethod]
    public void CancellationDuringMultipleOutput_StopsBeforeFurtherNativeRead()
    {
        FakeNative native = new(); native.OnSubmit = _ => { native.AddPcm(1); native.AddPcm(1); };
        using FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        using CancellationTokenSource cancellation = new();
        using var outputs = decoder.DecodeWave(Input(0, 2), cancellation.Token).GetEnumerator();
        Assert.IsTrue(outputs.MoveNext());
        cancellation.Cancel(); int reads = native.ReceiveCalls;
        Assert.ThrowsExactly<OperationCanceledException>(() => outputs.MoveNext());
        Assert.AreEqual(reads, native.ReceiveCalls);
    }

    [TestMethod]
    public void ResampledSourcePositions_AdvanceByActualPcmCount()
    {
        FakeNative native = new(); native.OnSubmit = _ => native.AddPcm(3);
        using FfmpegAacDecoder decoder = new(native, Pcm, 44100);
        WaveEntry[] entries = [.. decoder.DecodeWave(Input(44100, 8)), .. decoder.DecodeWave(Input(44108, 8))];
        CollectionAssert.AreEqual(new long?[] { 16000, 16003 }, entries.Select(e => e.StartSample).ToArray());
    }

    [TestMethod]
    public void PlanarPcm_PreservesBothPlanesAcrossSourceBoundary()
    {
        WaveFormat planar = new(SampleRate.Hz_16000, (WaveFormatEncoding)8, true);
        FakeNative native = new() { BytesPerSample = 4 };
        native.OnSubmit = count => { if (count == 2) native.AddPcm(4, 17); };
        using FfmpegAacDecoder decoder = new(native, planar, 16000);
        _ = decoder.DecodeWave(Input(0, 2)).ToArray();
        WaveEntry[] entries = decoder.DecodeWave(Input(2, 2)).ToArray();
        Assert.HasCount(2, entries);
        foreach (WaveEntry entry in entries)
        {
            Assert.AreEqual(8, entry.FrameData.Length); Assert.AreEqual(8, entry.FrameData2.Length);
            Assert.IsTrue(entry.FrameData.ToArray().All(b => b == 17));
            Assert.IsTrue(entry.FrameData2.ToArray().All(b => b == 18));
        }
    }

    [TestMethod]
    public async Task LinkedPipeline_CompletesOnlyAfterAllDelayedPcmIsForwarded()
    {
        FakeNative native = new(); native.OnSubmit = _ => { native.AddPcm(1); native.AddPcm(2); };
        native.OnFinish = () => { native.AddPcm(3); native.End(); };
        using AacToWave filter = new(new FfmpegAacDecoder(native, Pcm, 16000));
        Sink sink = new(); filter.LinkTo(sink);
        await filter.AddInputAsync(Input(0, 6)); await filter.CompleteAsync();
        Assert.AreEqual(6L, sink.Entries.Sum(e => (long)e.SamplesInFrame));
        Assert.IsTrue(sink.Flushed);
    }

    [TestMethod]
    public async Task EmptyLinkedPipeline_StillDrainsAndCompletes()
    {
        FakeNative native = new(); native.OnFinish = native.End;
        using AacToWave filter = new(new FfmpegAacDecoder(native, Pcm, 16000));
        Sink sink = new(); filter.LinkTo(sink); await filter.CompleteAsync();
        Assert.AreEqual(1, native.FinishCalls); Assert.IsEmpty(sink.Entries);
    }

    [TestMethod]
    public void Disposal_IsIdempotentAndRejectsFurtherDecode()
    {
        FakeNative native = new(); FfmpegAacDecoder decoder = new(native, Pcm, 16000);
        decoder.Dispose(); decoder.Dispose();
        Assert.AreEqual(1, native.Disposals);
        Assert.ThrowsExactly<ObjectDisposedException>(() => decoder.DecodeWave(Input(0, 1)).ToArray());
    }

    private static FrameEntry Input(long start, uint count, byte value = 1) => new()
    {
        Chunk = new ChunkEntry { TrackId = 1, ChunkIndex = 0, ChunkOffset = 0, FirstSample = start,
            ChunkSize = 1, FrameSizes = [1], FrameDurations = [count] },
        StartSample = start, SamplesInFrame = count, FrameData = new byte[] { value }, IsSyncSample = true,
    };

    private sealed class Sink : FrameFilterBase<WaveEntry>
    {
        protected override int InputBufferSize => 2;
        public List<WaveEntry> Entries { get; } = [];
        public bool Flushed;
        protected override Task HandleInputDataAsync(WaveEntry input) { Entries.Add(input); return Task.CompletedTask; }
        protected override Task FlushAsync() { Flushed = true; return Task.CompletedTask; }
    }

    private sealed class FakeNative : NativeDecode
    {
        protected override DecoderHandle Handle { get; } = new();
        public readonly Queue<int> SubmitResults = new(), FinishResults = new();
        public readonly List<byte[]> Submitted = [];
        private readonly Queue<(int status, int count, byte value)> outputs = new();
        public Action<int>? OnSubmit;
        public Action? OnFinish;
        public int FinishCalls, ReceiveCalls, Disposals;
        public int BytesPerSample = 4;
        public bool EndlessEmpty;
        public void AddPcm(int count, byte value = 1) => outputs.Enqueue((PcmReady, count, value));
        public void End() => outputs.Enqueue((EndOfStream, 0, 0));
        public override int SubmitPacket(ReadOnlyMemory<byte> data)
        {
            Submitted.Add(data.ToArray()); OnSubmit?.Invoke(Submitted.Count);
            return SubmitResults.TryDequeue(out int result) ? result : Accepted;
        }
        public override int FinishInput()
        {
            FinishCalls++; OnFinish?.Invoke();
            return FinishResults.TryDequeue(out int result) ? result : Accepted;
        }
        public override int ReceivePcm(Span<byte> first, Span<byte> second, int capacity, out int samples)
        {
            ReceiveCalls++; samples = 0;
            if (EndlessEmpty) return PcmConsumed;
            if (!outputs.TryPeek(out var output)) return NeedInput;
            samples = output.count;
            if (output.status != PcmReady) { outputs.Dequeue(); return output.status; }
            if (first.IsEmpty) return PcmReady;
            Assert.IsGreaterThanOrEqualTo(samples, capacity);
            first[..(samples * BytesPerSample)].Fill(output.value);
            if (!second.IsEmpty) second[..(samples * BytesPerSample)].Fill((byte)(output.value + 1));
            outputs.Dequeue(); return PcmConsumed;
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; }
    }
}
