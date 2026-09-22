# AAC format admission

The matched native/managed graph exposes `Decoder_GetInputFormat`: after the first
source frame is staged, it reports the decoder's source sample rate, channel count
and native channel mask. This differs from the requested resampled PCM output.
Before input it returns `DECODER_NEED_INPUT`; invalid arguments and terminal errors
preserve caller output values. Non-native layouts have mask zero.

AAC PCM conversion admits declared mono/stereo sources only and compares the
first real decoded format with that declaration before converting or delivering
PCM. PCE/multichannel layouts and effective rate/layout mismatches are rejected
explicitly. This prevents an unresolved SBR/PS rate or channel expansion from
silently using a guessed output format. It does not implement effective-profile
negotiation or establish HE-AAC, xHE-AAC, PCE or multichannel support. Existing
native v2 handling rejects subsequent decoded format changes terminally.
Explicit resampling remains supported when the source format itself matches.
E-AC-3/AC-4 source selection is unchanged by this AAC guard.

An old native payload without the input-format export fails before AAC PCM is
delivered. Package admission requires the export and `input_format_pass` execution
attestation, in addition to drain/error/encoder timing evidence. Rebuild the exact
managed/parser/native set under an isolated local identity; do not replace bytes
under an official version.

The encoder and parser writer also reject output rates above 65,535 Hz rather
than narrowing the MP4 sample entry's 16.16 field. The writer validates null,
truncated, ambiguous and unsupported configurations before touching the output
stream. Admitting higher rates requires a separate sample-entry representation.

Reproduction:

- Run hermetic `AAXClean.Codecs.Test` excluding external `HP_CodecTest` fixtures.
  `DecoderFormatAdmissionTests` cover constructor admission;
  `DecoderDrainTests` cover actual-format mismatch, explicit resampling, missing
  export and resource ownership through the managed transport seam.
- Build with `src/AAXCleanNative/build-native.sh ARTIFACTS release osx-arm64` on
  this host, then run `native-receipt.py verify`. Native drain cases compare real
  AAC/E-AC-3 source format against separately selected PCM output formats.
- Run `src/AAXCleanNative/tests/drain-managed/DecoderDrainSmoke.csproj` with the
  matched library, generated fixtures and `v2`, plus `reject-format-change`.
  `reject-input-format` must use a retained older v2 library without the getter.
- Run package gate and native receipt unit tests. These validate attestations;
  they do not replace matching-platform native execution.

The parser's `AacOutputAdmissionTests` independently cover untouched output on
rejection and exact metadata at 64 kHz, 44.1 kHz and 7.35 kHz. Only synthetic,
redistributable local fixtures are used. Real provider/profile, foreign RID,
installed app and release proof remain separate.
