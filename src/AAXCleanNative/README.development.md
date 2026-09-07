# Pinned macOS arm64 development build and native safety checks

This build is an isolated development and sanitizer target. It does not replace `bins/`, identify the source of the six tracked payloads, create a distributable package, or establish other-RID compatibility. The configuration enables FDK AAC and is marked **nonfree and unredistributable** by Librempeg. Distribution and dependency/advisory admission remain separate work.

## Inputs and prerequisites

`native-sources.json` pins exact Librempeg/FDK commits, the LAME 3.100 release archive, download URLs and SHA-256 hashes. `prepare-native-sources.py` verifies TLS, archive hashes, and each extracted source tree against its archive before every build. A modified or additional source file makes preparation fail. Builds use separate output directories and leave the inputs intact.

The current target requires macOS arm64, Xcode command-line tools/SDK, Python 3.12+ (tar extraction filters), CMake (default `/opt/homebrew/bin/cmake`, override with `CMAKE`), `make`, and .NET 10 for managed tests. `rg` is needed for regression-witness classification. Build defaults are deployment target 14.0, eight jobs (override `JOBS`), `ZERO_AR_DATE=1` and a fixed `SOURCE_DATE_EPOCH`. The exact compiler, SDK, OS and flags are recorded per build. Matching inputs and commands make the development build repeatable; cross-host byte reproducibility has not been established.

The pinned Librempeg revision requires `avfilter` for `avcodec`; the recipe explicitly enables both. AAC uses FDK, E-AC-3 and AC-4 use Librempeg. Autodetection, network, programs, assembly and unused media components are disabled. pthread support remains enabled. FDK and LAME are built locally and linked statically. When pkg-config is absent, Librempeg's supported direct header/symbol checks use only that build's explicit include/library prefix. No synthetic configure success is injected.

LAME 3.100 preserves the existing recipe's dependency choice for this bounded development target; it is not claimed to be the newest release. Vendor source warnings are retained in the build logs, not suppressed or corrected as part of the wrapper change.

## Commands

Run from the AAXClean.Codecs checkout with the accepted AAXClean sibling beside it:

```sh
bash src/AAXCleanNative/build-osx-arm64.sh ../artifacts/native asan
bash src/AAXCleanNative/tests/run-native-safety.sh ../artifacts/native
bash src/AAXCleanNative/tests/run-managed-safety.sh ../artifacts/native
bash src/AAXCleanNative/tests/run-regression-witness.sh ../artifacts/native 486ecf6798465f09a7504c66209ac97a88e55319
bash src/AAXCleanNative/build-osx-arm64.sh ../artifacts/native release
dotnet ../artifacts/native/managed/bin/NativeHandleSafety/debug/NativeHandleSafety.dll ../artifacts/native/release/libaaxcleannative.dylib smoke
```

Here `release` names the ordinary optimized **development** build mode. Its artifacts stay under the specified root. It is not release delivery.

The managed runner disables shared build servers and uses isolated artifacts to avoid changing the sibling's ordinary build products. Its project compiles the five production interop files directly and references the accepted sibling AAXClean project; it does not introduce a product test seam or replace native behavior with a managed mock. The smoke mode loads the ordinary production dylib directly through those same interop methods. No library is installed into the system.

## Ownership and tests

The existing exported pointer ABI is retained. Open returns either an owned positive handle or a negative error code. A successful raw handle must be closed exactly once; closing NULL is allowed. Repeatedly closing a freed raw pointer is not supported. Managed wrappers allocate an empty SafeHandle before invoking native open, adopt only successful pointers, and leave failure sentinels unowned.

`Decoder_DecodeFrame` borrows the input only during the call. It rejects null, empty and unrepresentable lengths, copies valid input into an `av_new_packet` allocation with zeroed `AV_INPUT_BUFFER_PADDING_SIZE` padding, sends it, and releases its packet reference on every send result. The decoder may retain its own reference. AAC extradata uses the same required zero padding and is owned/freed by its codec context. Failed initialization publishes no handle, leaving one cleanup owner.

The native harness interposes only wrapper allocation/open/send calls to inject failures and observe ownership; the pinned codec implementations run normally. It includes:

- ABI struct sizes/offsets and all three decoder opens.
- Two-byte ASC and actual encoded AAC packets placed at the end of a readable page followed by a protected page.
- Payload equality, native packet ownership, zero padding, and a retained packet reference that survives caller-buffer unmapping.
- Seventeen truncated/malformed sizes for each AAC/E-AC-3/AC-4 decoder.
- Null/zero/overflow sizes, packet allocation failure and send EAGAIN cleanup.
- Twenty-five allocation/find/open failure points across three decoders and the encoder, with wrapper/context/frame/packet allocation balance.

The managed harness runs 34 explicit cases: ABI, 25 failed-open/finalization checks, four repeated-dispose checks, and four abandoned-wrapper finalization checks. Test-only close counters prove error sentinels never reach close and successful handles close once. Test-only exports are confined to `libnative-safety.dylib`; they are absent from the production wrapper source.

The regression script compiles the old native wrapper against the same pinned libraries. It requires three specific old-code failures: ASC padding read diagnosed by ASAN as heap-buffer-overflow, missing native packet ownership diagnosed by the harness assertion, and failed-open cleanup diagnosed by ASAN as heap-use-after-free. A random crash or unexpected pass is rejected. The first two are explicit contract witnesses; the packet API can copy non-reference-counted inputs internally, so the ownership assertion alone does not prove an actual decoder retained a dangling caller pointer.

ASAN compiles the wrapper and dependency C/C++ code with `-fsanitize=address`; Librempeg assembly is disabled. LeakSanitizer is disabled on this macOS run. Allocation accounting covers the wrapper-owned resources, not every internal codec allocation. No real Audible content, full-stream PCM equivalence, drain/priming/tail correctness, malformed corpus completeness, provider path, other-RID run, packaging, signing or release proof is implied. The bounded smoke feeds 12 AAC packets without an end-of-stream drain and may report queued encoder frames on close; that behavior belongs to the separate drain work package.
