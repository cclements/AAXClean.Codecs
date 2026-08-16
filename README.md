# AAXClean.Codecs
Converts and filters aac audio from [AAXClean](https://github.com/Mbucari/AAXClean).

**Supported Codecs**
| |Decode|Encode|
|-|-|-|
|AAC-LC|:heavy_check_mark:|:heavy_check_mark:|
|AC-4|:heavy_check_mark:||
|E-AC-3|:heavy_check_mark:||
|HE-AAC|:heavy_check_mark:||
|USAC|:heavy_check_mark:||
|xHE-AAC|:heavy_check_mark:||
|AAC-ELD|:heavy_check_mark:||
|MP3||:heavy_check_mark:|

**Supported Platforms**
| |x64|Arm 64|
|-|-|-|
|Windows|:heavy_check_mark:|:heavy_check_mark:|
|macOS|:heavy_check_mark:|:heavy_check_mark:|
|Linux|:heavy_check_mark:|:heavy_check_mark:|

## Nuget
Include the [AAXClean.Codecs](https://www.nuget.org/packages/AAXClean.Codecs/) NuGet package to your project.

### Companion AAXClean dependency for the presentation-timing changes

This branch consumes companion AAXClean presentation APIs throughout, not only for
multipart output. Every conversion entry point validates the source window through
`Mpeg4File.PresentationStartSample`/`PresentedDurationSamples`, sample mapping and the
decoder's coordinate rescaling use the exact scaler `ElstBox.ScaleDuration`, and the
multipart adapter consumes the tagged `MultipartFilterBase` constructor and the multipart
split seams. AAC timing also uses AAXClean's MP4 edit-list writer
(`Mp4aWriter.SetEditList`), which is already present in AAXClean 3.1.0 -- but none of the
presentation surface above is. A Release build against the published 3.1.0 package fails
with exactly six `CS0115` errors (the three multipart override seams across both target
frameworks); those declaration-phase errors mask the remaining missing members, which
surface only once the seams resolve. This branch is therefore not a standalone package
update and must not be released against AAXClean 3.1.0.

Release AAXClean first under an unused version chosen by the package owner, update both
the Release `PackageReference` and this repository's nuspec to that exact version, give
this package its own new version, then build and pack AAXClean.Codecs in Release
configuration. The repository's Debug build
uses a sibling AAXClean source checkout for development; a successful Debug build is
therefore source-integration evidence, not proof that the published package dependency
is compatible.

## Usage:

```C#
using AAXClean.Codecs;

var audible_key = "aa0b0c0d0e0f1a1b1c1d1e1f2a2b2c2d";
var audible_iv = "ce2f3a3b3c3d3e3f4a4b4c4d4e4f5a5b";
aaxcFile.SetDecryptionKey(audible_key, audible_iv);
```
### Convert to Mp3:
```C#
await aaxcFile.ConvertToMp3Async(File.Open(@"C:\Decrypted book.mp3", FileMode.OpenOrCreate, FileAccess.ReadWrite));
```
Note that the output stream must be Readable, Writable and Seekable for the mp3 Xing header to be written. See [NAudio.Lame #24](https://github.com/Corey-M/NAudio.Lame/issues/24)

### Convert to AAC-LC:
```C#
var options = new AacEncodingOptions
{
	EncoderQuality = 0.5,
	BitRate = 30000,
	Stereo = false,
	SampleRate = SampleRate.Hz_16000
};

await mp4.ConvertToMp4aAsync(File.OpenWrite(@"C:\Decrypted book.mp4"), options);
```

AAC encoding consumes packed (interleaved) PCM. Planar PCM is rejected before native
encoding because the managed path cannot safely infer or combine separate channel
planes. Input entries larger than one 1,024-sample AAC frame are divided at byte offsets
derived from the PCM block alignment; this prevents later chunks from overlapping PCM
that was already encoded.

### Detect Silence
```C#
await aaxcFile.DetectSilenceAsync(-30, TimeSpan.FromSeconds(0.25));
```


### Conversion Usage:
```C#
var mp4File = new Mp4File(File.OpenRead(@"C:\Decrypted book.m4b"));
await mp4File.ConvertToMp3Async(File.OpenWrite(@"C:\Decrypted book.mp3"));
```
### Multipart Conversion Example:
Note that the input stream needs to be seekable to call GetChapterInfo()

```C#
var chapters = aaxcFile.GetChaptersFromMetadata();
await aaxcFile.ConvertToMultiMp4aAsync(chapters, NewSplit);
            
private static void NewSplit(NewSplitCallback newSplitCallback)
{
	string dir = @"C:\book split\";

	string fileName = newSplitCallback.Chapter.Title.Replace(":", "") + ".m4b";

	newSplitCallback.OutputFile = File.OpenWrite(Path.Combine(dir, fileName));
}
```
