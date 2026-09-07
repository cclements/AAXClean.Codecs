using AAXClean.Codecs.FrameFilters.Audio;
using Mpeg4Lib.Boxes;
using System;
using System.Runtime.InteropServices;

namespace AAXClean.Codecs.Interop;

internal class NativeAacDecode : NativeDecode
{
	// Allocate the SafeHandle before opening native resources, and adopt only success.
	protected override DecoderHandle Handle { get; } = new();

	public unsafe NativeAacDecode(EsdsBox esed, WaveFormat waveFormat)
	{
		ArgumentNullException.ThrowIfNull(esed, nameof(esed));
		ArgumentNullException.ThrowIfNull(waveFormat, nameof(waveFormat));

		var asc = esed.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob;
		fixed (byte* pAsc = asc)
		{
			AacDecoderOptions options = new()
			{
				output_options = GetOutputOptions(waveFormat),
				asc_size = asc.Length,
				ASC = pAsc
			};
			Handle.Initialize(Decoder_OpenAac(ref options), "AAC");
		}
	}

	[DllImport(libname, CallingConvention = CallingConvention.StdCall)]
	private static extern IntPtr Decoder_OpenAac(ref AacDecoderOptions decoder_options);

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct AacDecoderOptions
	{
		public OutputOptions output_options;
		public int asc_size;
		public byte* ASC;
	}
}
