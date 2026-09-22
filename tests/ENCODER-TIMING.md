# Encoder timing verification

AAC encoding requires the additive `AacEncoder_GetTiming` export as well as the
decoder v2 payload. The native contract reports frame size and initial codec delay
in samples per channel. A payload missing the export is rejected and its opened
handle is released. Emitted frame counts are not a delay measurement.

The current pinned FDK build reports 2,048 initial samples for the tested AAC-LC
mono/stereo settings. This is an observation, not a hardcoded value or a claim for
other encoders/profiles. The synthetic fractional-delay harness proves managed
code uses the reported delay instead of rounding it to a packet boundary.

`src/AAXCleanNative/tests/encoder-timing-tests.c` loads the actual candidate and
checks ten short/exact/partial input cases, complete encoded coverage and invalid
arguments. The pinned matching-host build and receipt producer run it, and package
admission requires its execution receipt. This native test does not itself prove
signal alignment. `run-native-safety.sh` and `run-managed-safety.sh` cover the new
contract under ASan, including invalid/missing timing and resource cleanup.

For signal proof, build a matching native library, set `DYLD_LIBRARY_PATH` to its
private directory on macOS, and run the hermetic Codecs tests against the intended
parser source/package. Set `AAXCLEAN_SIGNAL_OUTPUT` to a **fresh absolute directory**
so `EncoderSignalTests` keeps its PCM/MP4/metadata outputs after test cleanup. The
nine single-file cases and three chapter outputs use generated waveforms only.
The writer must include the output-movie-timescale correction when the output
sample rate differs from the source clock.

Then run:

```sh
python3 tests/verify-encoder-signals.py "$AAXCLEAN_SIGNAL_OUTPUT" /absolute/path/signal-receipt.json
```

The verifier requires NumPy and an independent `ffmpeg` on PATH (or `--ffmpeg`). It
checks hashes, exact presented/media sample counts, independently searches for
codec delay, and compares whole/head/tail audio with the original PCM, including
each standalone chapter. It removes an old success receipt before verification;
missing cases, decode failures and misaligned signals cannot produce a success.
The one-sample case checks exact count and amplitude rather than claiming a
meaningful correlation measurement. These are synthetic local AAC-LC checks, not
provider/profile conformance, listening acceptance or all-platform proof.
