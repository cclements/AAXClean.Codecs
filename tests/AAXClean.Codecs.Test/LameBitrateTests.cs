using AAXClean.FrameFilters.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Lame;

namespace AAXClean.Codecs.Test;

[TestClass]
public class LameBitrateTests
{
    [TestMethod]
    [DataRow(128000, 1, false, 128)]
    [DataRow(128000, 2, false, 64)]
    [DataRow(64000, 1, false, 64)]
    [DataRow(192000, 2, false, 96)]
    [DataRow(128000, 2, true, 128)]
    public void DefaultAbrUsesDecimalKbpsAndRetainsExistingChannelAndUsacScaling(
        int bitsPerSecond, int channels, bool usac, int expectedKbps)
    {
        using var seed = new Mp4File(new MemoryStream(AacPresentationWindowTests.CreateSourceMp4()));
        using var output = new MemoryStream();
        // Structural metadata only; no AAC/USAC decoding claim.
        byte[] asc = usac ? Convert.FromHexString("F9464800") : [0x12, (byte)(channels << 3)];
        using (var writer = new Mp4aWriter(output, seed.Ftyp, seed.Moov, asc))
        {
            writer.AddFrame([0], true, 1024);
            writer.Close();
        }
        using var source = new Mp4File(new MemoryStream(output.ToArray()));
        source.AudioSampleEntry.Esds!.ES_Descriptor.DecoderConfig.AverageBitrate = (uint)bitsPerSecond;
        Assert.AreEqual(bitsPerSecond, source.AverageBitrate);
        Assert.AreEqual(channels, source.AudioChannels);
        var options = source.GetDefaultLameConfig();
        Assert.AreEqual(expectedKbps, options.ABRRateKbps);
        Assert.AreEqual(VBRMode.ABR, options.VBR);
        Assert.AreEqual(MPEGMode.Mono, options.Mode);
    }
}
