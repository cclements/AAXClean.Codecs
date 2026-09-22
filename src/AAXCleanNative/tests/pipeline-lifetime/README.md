# AAC pipeline native lifetime contract

The console harness loads the real `native-safety` probe into the production
Codecs assembly. It counts closed encoders, live wrapper allocations and live
codec contexts/frames/packets while garbage collection is disabled. Each case
runs in its own process. This proves prompt ownership cleanup, not eventual
SafeHandle finalization. The probe wraps the actual native implementation and
FDK codec; no codec behavior is mocked.

Ten cases cover single and multipart completion, output-construction failure,
short-input flush failure and longer-input encoding/write failure. Multipart
success uses three chapters and requires zero live resources before every next
output callback. Fault cases preserve the exact injected exception and verify
that the intended pipeline stage threw. Two additional chain cases inject an upstream transform failure after actual AAC
payload has been written and verify that single/multipart downstream encoders
finish and close before completion returns. These require the corrected parser
filter lifecycle; Codecs production and native bytes are unchanged.
All cases require exactly one native
close per opened encoder before filter disposal, then verify repeated disposal
cannot close a handle again.

Build `PipelineLifetime.csproj` with the matching parser source/package graph.
The standalone harness supports net10.0 and net8.0; its explicit Major roll-forward
allows the latter to run on a newer installed runtime but does not establish a
.NET 8 runtime result. Pass a small generated AAC-LC MP4, for example an output
from `EncoderSignalTests`; provider media is neither needed nor appropriate.

On macOS, run the maintained wrapper once for each built target:

```sh
src/AAXCleanNative/tests/run-pipeline-lifetime.sh \
  /absolute/build/AAXClean.Codecs.Test.dll \
  /absolute/asan/tests/libnative-safety.dylib \
  /absolute/generated/single-1501-16000-1.m4a \
  /absolute/results
```

The wrapper enables ASan, disables global leak scanning for the managed host and
requires every case to pass. Explicit allocation counters remain mandatory.
The `libnative-safety` probe comes from `run-native-safety.sh` and must match the
candidate native sources/exports. Ordinary native output signal verification
remains a separate check. The expected FDK message about queued frames on an
injected output failure is not an ASan report; the encoder is deliberately closed
without publishing a successful file.

September 22, 2026: previous Codecs `d6f65f6` fails all eight prompt-lifetime cases;
the corrected source passes all eight on both managed targets under ASan, with
.NET 10.0.10 hosting both. Full Codecs consumers pass 65/65 per target and twelve
independently decoded generated files per target. The correction does not change
native bytes, sample timing, stream ownership or the public filtering API.

The chain contract also requires exact original exception identity, successful
joining of the linked worker and repeated completion/disposal. These are failed
operations; flushing a buffered prefix during cleanup is not successful output
publication or recovery-journal proof.
