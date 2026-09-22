# MP3 encoder bitrate units

`Mpeg4File.AverageBitrate` is bit/s. LAME CBR and ABR controls accept decimal
kbit/s, so the conversion divides by 1000. This matches the
[FFmpeg LAME adapter](https://www.ffmpeg.org/doxygen/trunk/libmp3lame_8c_source.html#l00127).
A 128000-bit/s mono source now requests 128 kbit/s rather than 125.

The existing default mono/channel scaling and USAC multiplier remain unchanged.
`LameBitrateTests` covers five AAC/USAC metadata configurations: all five fail
against the preceding conversion and pass after correction. Fixtures are
structural metadata, not encoded USAC media or quality comparisons.

This change does not alter serialized historical library bitrate fields, source
selection, VBR quality, presets or channel defaults. Historical metadata unit
migration, source effective-format negotiation and measured cross-codec quality
remain separate work. The native encoder can still negotiate supported rates;
these tests verify the requested configuration, not every emitted MP3 bitrate.
