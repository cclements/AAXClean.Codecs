#nullable enable
using AAXClean.Codecs.FrameFilters.Audio;
using Mpeg4Lib.Boxes;

namespace AAXClean.Codecs.Test;

[TestClass]
public class DecoderFormatAdmissionTests
{
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(3, false)]
    [DataRow(7, false)]
    [DataRow(0, true)]
    [DataRow(3, true)]
    [DataRow(7, true)]
    public void Ambiguous_or_multichannel_aac_is_rejected_before_native_open(int channels, bool explicitOutput)
    {
        using var source = new Mp4File(new MemoryStream(AacPresentationWindowTests.CreateSourceMp4()));
        var entry = source.AudioSampleEntry;
        var esds = EsdsBox.CreateEmpty(entry);
        esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.AscBlob = [0x12, 0x10];
        esds.ES_Descriptor.DecoderConfig.AudioSpecificConfig.ChannelConfiguration = channels;
        FfmpegAacDecoder? decoder = null;
        try
        {
            var error = Assert.ThrowsExactly<NotSupportedException>(() => decoder = explicitOutput
                ? new FfmpegAacDecoder(entry, 44100, WaveFormatEncoding.Pcm, SampleRate.Hz_16000, true)
                : new FfmpegAacDecoder(entry, 44100, WaveFormatEncoding.Pcm));
            StringAssert.Contains(error.Message, "source layout");
        }
        finally { decoder?.Dispose(); }
    }

    [TestMethod]
    [DataRow(96000)]
    [DataRow(88200)]
    public void Unrepresentable_mp4_output_rate_is_rejected_before_encoder_open(int rate)
    {
        FfmpegAacEncoder? encoder = null;
        try
        {
            var error = Assert.ThrowsExactly<NotSupportedException>(() =>
                encoder = new FfmpegAacEncoder(new WaveFormat((SampleRate)rate, WaveFormatEncoding.Pcm, true), 128000, null));
            StringAssert.Contains(error.Message, "MP4");
        }
        finally { encoder?.Dispose(); }
    }
}
