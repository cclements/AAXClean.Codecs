# Package admission

The checked-in `artifacts.json` deliberately blocks packing: there is no accepted
six-RID native v2 set. Do not populate missing cells from `bins/`. Build and unit
test remain usable; `GenerateNuspec` requires preflight, including `pack --no-build`.
The `Pack` target also validates the resulting archive automatically. The normal
workflow runs the hermetic gate regressions and checks the archive before
uploading or publishing it. This change does not publish anything.

Prepare a private staging directory and explicit nuspec. Every nuspec source is
relative to that directory, every target is a complete archive filename (including
README/license targets), and wildcards are forbidden. Supply absolute paths to
MSBuild's `NuspecFile`, `NuspecBasePath`, `PackageAdmissionRoot` and
`PackageArtifactManifest`; set `PackageVersion` to the manifest version. Set
`NuspecProperties=version=VERSION` if the spec uses `$version$`. All parser
references in the staged nuspec must be exact `[VERSION]` pins, one in each of
exactly two distinct `net8.0` and `net10.0` dependency groups. The archive must
preserve those group assignments. Build against that
same parser before obtaining the execution receipts. Never reuse public 3.1.0.

Manifest schema 1 has `lane` (`shipping` or `local`), `version`, `parser` and
`files`. `parser` holds `path`, `sha256`, and `version` of the accepted AAXClean
nupkg. Each file holds staging-relative `path`, `sha256`, and archive `target`.
Both managed TFMs and both managed assemblies are mandatory. Each native file
also holds a `receipt` object with the path and hash of a reviewed JSON receipt.
The test fixture builder in `test_preflight.py` is an executable schema example;
its synthetic bytes/receipts are not platform evidence.

A native receipt binds `rid`, `native_sha256`, the complete `managed` target/hash
map, and `parser_sha256`. It records `api_version: 2`, the three v2 `exports`,
`execution_host_rid`, `drain_pass: true`, `error_pass: true`, and nonempty identity
strings for `source`, `dependencies`, `configuration`, and `toolchain`. Use exact
commits/hashes and build flags in those strings. `execution_log` is a path/hash
record for retained matching-host execution and export inspection. Shipping
requires `distribution: approved` plus a hashed `distribution_record`. Local
receipts may instead declare `NONFREE/UNREDISTRIBUTABLE`.

These are trusted, owner-reviewed build/execution attestations. The validator
checks their consistency and artifact hashes; it cannot establish that arbitrary
receipt prose is true. It inspects executable architecture directly, but does
not load foreign binaries or mistake a symbol string for a real export/runtime
check. The matching-RID producer must inspect exports and execute version,
drain and failure consumers before issuing a receipt. Provenance signing and
remote attestation collection are outside this bounded gate.

Shipping requires all six existing RIDs, each exactly once. Local requires one
RID and a unique `-local.` version; it does not confer distribution admission.
The existing osx-arm64 NONFREE artifact remains local-only. No new RID evidence
is created by this tool. Packaging and publication remain separate operations.

Run `python3 -m unittest discover -s tools/packaging -p 'test_*.py' -v`.
Run `python3 tools/packaging/preflight.py --root STAGE --manifest MANIFEST
--nuspec SPEC --version VERSION` before packing; add `--archive PACKAGE.nupkg`
after packing. The archive check must pass before any delivery, including local
consumer rehearsal. For Windows, `PackageAdmissionPython` can select the local
Python executable. Python 3.9+ is required.
