using AAXClean.FrameFilters;
using Mpeg4Lib.Boxes;
using System;
using System.Threading;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace AAXClean.Codecs.FrameFilters.Audio;

// A compressed packet can produce zero or many PCM outputs. This filter owns
// expansion locally; the shared one-to-one parser transform contract is unchanged.
internal sealed class AacToWave : FrameFilterBase<FrameEntry>
{
    protected override int InputBufferSize => 300;
    public WaveFormat WaveFormat => AacDecoder.WaveFormat;
    private readonly FfmpegAacDecoder AacDecoder;
    private FrameFilterBase<WaveEntry>? Linked;
    private CancellationToken CancellationToken;
    private bool HasInput;
    private ExceptionDispatchInfo? Failure;

    public AacToWave(AudioSampleEntry audioSampleEntry, uint inputTimescale, WaveFormatEncoding waveFormat, SampleRate sampleRate, bool stereo)
        => AacDecoder = new FfmpegAacDecoder(audioSampleEntry, inputTimescale, waveFormat, sampleRate, stereo);
    public AacToWave(AudioSampleEntry audioSampleEntry, uint inputTimescale, WaveFormatEncoding waveFormat)
        => AacDecoder = new FfmpegAacDecoder(audioSampleEntry, inputTimescale, waveFormat);
    internal AacToWave(FfmpegAacDecoder decoder) => AacDecoder = decoder;

    public void LinkTo(FrameFilterBase<WaveEntry> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        Linked = next;
        Linked.SetCancellationToken(CancellationToken);
    }
    public override void SetCancellationToken(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        base.SetCancellationToken(cancellationToken);
        Linked?.SetCancellationToken(cancellationToken);
    }
    public override async Task AddInputAsync(FrameEntry input)
    {
        CancellationToken.ThrowIfCancellationRequested();
        HasInput = true;
        await base.AddInputAsync(input);
    }
    protected override async Task HandleInputDataAsync(FrameEntry input)
    {
        try
        {
            foreach (var output in AacDecoder.DecodeWave(input, CancellationToken))
                await ForwardAsync(output);
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref Failure, ExceptionDispatchInfo.Capture(error), null);
            throw;
        }
    }
    protected override async Task FlushAsync()
    {
        try
        {
            foreach (var output in AacDecoder.DecodeFlush(CancellationToken))
                await ForwardAsync(output);
        }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref Failure, ExceptionDispatchInfo.Capture(error), null);
            throw;
        }
    }
    private Task ForwardAsync(WaveEntry output)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Linked?.AddInputAsync(output)
            ?? throw new InvalidOperationException("The decoder filter must be linked before processing audio.");
    }
    protected override async Task CompleteInternalAsync()
    {
        try
        {
            await base.CompleteInternalAsync();
            if (!HasInput)
                await FlushAsync();
            await (Linked?.CompleteAsync() ?? Task.CompletedTask);
        }
        catch
        {
            // The shared channel owner can report ChannelClosedException while
            // closing an already-failed writer. Preserve this filter's original
            // decoder/forwarding failure instead of losing the useful cause.
            Failure?.Throw();
            throw;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            AacDecoder.Dispose();
            Linked?.Dispose();
        }
        base.Dispose(disposing);
    }
}
