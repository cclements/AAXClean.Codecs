using AAXClean.Codecs.FrameFilters.Audio;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AAXClean.Codecs.Interop;

internal unsafe abstract class NativeDecode : IDisposable
{
	internal const int Accepted = 0, ReceiveFirst = 1;
	internal const int InputFormatChanged = -13;
	internal const int PcmConsumed = 0, NeedInput = 1, PcmReady = 2, EndOfStream = 3;
	protected abstract DecoderHandle Handle { get; }
	protected const string libname = "aaxcleannative";

	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_DecodeFrame(DecoderHandle self, byte* pCompressedAudio, int cbInBufferSize);

	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_ReceiveDecodedFrame(DecoderHandle self, byte* pDecodedAudio1, byte* pDecodedAudio2, int cbInBufferSize);

	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_DecodeFlush(DecoderHandle self, byte* pDecodedAudio1, byte* pDecodedAudio2, int cbInBufferSize);

	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_GetApiVersion();
	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_SubmitPacket(DecoderHandle self, byte* data, int size);
	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_ReceivePcm(DecoderHandle self, byte* output0, byte* output1, int capacity, out int samples);
	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern int Decoder_GetInputFormat(DecoderHandle self, out int sampleRate, out int channels, out ulong channelMask);

	internal readonly record struct InputFormat(int SampleRate, int Channels, ulong ChannelMask);
	public virtual InputFormat GetInputFormat()
	{
		try
		{
			if (Decoder_GetInputFormat(Handle, out int rate, out int channels, out ulong mask) != 0)
				throw new InvalidDataException("Native decoder has not reported a decoded input format.");
			return new(rate, channels, mask);
		}
		catch (EntryPointNotFoundException error)
		{
			throw new PlatformNotSupportedException("AAC PCM conversion requires a matched native payload with Decoder_GetInputFormat.", error);
		}
	}

	internal static void EnsureDrainApi()
	{
		const string message = "PCM conversion requires matched native decoder API v2. Replace the old or mismatched native codec payload before converting audio.";
		try
		{
			if (Decoder_GetApiVersion() != 2)
				throw new PlatformNotSupportedException(message);
		}
		catch (EntryPointNotFoundException error)
		{
			throw new PlatformNotSupportedException(message, error);
		}
	}

	public virtual int SubmitPacket(ReadOnlyMemory<byte> data)
	{
		fixed (byte* input = data.Span)
			return Decoder_SubmitPacket(Handle, input, data.Length);
	}
	public virtual int FinishInput() => Decoder_SubmitPacket(Handle, null, 0);
	public virtual int ReceivePcm(Span<byte> output0, Span<byte> output1, int capacity, out int samples)
	{
		fixed (byte* first = output0)
		fixed (byte* second = output1)
			return Decoder_ReceivePcm(Handle, first, second, capacity, out samples);
	}

	public int DecodeFrame(byte* pCompressedAudio, int cbInputSize)
		=> Decoder_DecodeFrame(Handle, pCompressedAudio, cbInputSize);
	public int ReceiveDecodedFrame(byte* pDecodedAudio1, byte* pDecodedAudio2, int cbInputSize)
	{
		int receivedSamples = Decoder_ReceiveDecodedFrame(Handle, pDecodedAudio1, pDecodedAudio2, cbInputSize);
		//Debug.Assert(receivedSamples <= cbInputSize);
		return receivedSamples >= 0 ? receivedSamples
			: throw new Exception($"Error receiving decoded frame. Code {GetFFmpegErrorString(receivedSamples)}");
	}
	public int DecodeFlush(byte* pDecodedAudio1, byte* pDecodedAudio2, int cbInputSize)
	{
		int receivedSamples = Decoder_DecodeFlush(Handle, pDecodedAudio1, pDecodedAudio2, cbInputSize);
		//Debug.Assert(receivedSamples <= cbInputSize);
		return receivedSamples >= 0 ? receivedSamples
			: throw new Exception($"Error receiving decoded frame. Code {GetFFmpegErrorString(receivedSamples)}");
	}
	public static string GetFFmpegErrorString(int errorCode)
		=> System.Text.Encoding.UTF8.GetString(BitConverter.GetBytes(-errorCode));

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(bool disposing)
	{
		if (disposing && !Handle.IsClosed)
			Handle.Close();
	}

	[StructLayout(LayoutKind.Sequential)]
	protected struct OutputOptions
	{
		public int out_sample_rate;
		public int out_sample_fmt;
		public int out_channels;
	}

	protected static OutputOptions GetOutputOptions(WaveFormat waveFormat)
	{
		if (waveFormat.Channels is not 1 and not 2)
			throw new ArgumentException("Output wave format must be either mono or stereo.");

		return new OutputOptions
		{
			out_sample_rate = waveFormat.SampleRate,
			out_sample_fmt = (int)waveFormat.Encoding,
			out_channels = waveFormat.Channels,
		};
	}

	protected class DecoderHandle : SafeHandle
	{
		[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
		private static extern int Decoder_Close(IntPtr self);
		public DecoderHandle() : base(IntPtr.Zero, true) { }
		public void Initialize(IntPtr nativeHandle, string codec)
		{
			if (nativeHandle.ToInt64() <= 0)
				throw new Exception($"Error opening {codec} Decoder. Code {nativeHandle.ToInt64()}");
			SetHandle(nativeHandle);
		}
		public override bool IsInvalid => handle.ToInt64() <= 0;
		protected override bool ReleaseHandle() => Decoder_Close(handle) == 0;
	}
}
