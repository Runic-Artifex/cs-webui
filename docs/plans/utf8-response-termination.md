# UTF-8 native response termination

Discovered during the authorized CLI/Translations developer-experience work on
2026-10-05. Native hosted Editor responses contained valid JSON followed by stray
bytes, preventing reliable plural preview/save acceptance.

## Cause and repair

Published `CsWebUi 2.5.0-beta.4.5`, source commit
`cb751354d1dea81fff1f8e9bd2a336fc0407942e`, allocates an uninitialized UTF-8
buffer with one extra byte but does not write its terminator. `ReturnString`
pins the buffer and passes it to a native C-string API, which scans until NUL.
Independent source and actual native-library inspection confirm this path.

Write `bytes[byteCount] = 0` after encoding. Preserve public API, native ABI,
embedded-NUL rejection and the pinned native source/assets. Prepare managed and
native packages consistently as `2.5.0-beta.4.6`.

## Evidence

- Regression covers empty/ASCII/Unicode and 128 large Unicode responses under
  heap reuse, checking exact UTF-8 bytes, terminator and native C-string result.
- Before repair: focused tests 4 passed/1 failed; terminator byte was 110.
- After repair: focused tests 5/5; managed/native tests 29/29, no skips.
- Independent review approves the patch and isolated candidate scope.
- Temporary candidate feed and provenance:
  `/tmp/runic-cswebui-utf8-20261005/feed` and `candidate-validation.json`.
  Candidates retain all seven native assets from the SHA-512-verified published
  package, with individual SHA-256 evidence. This is local verification evidence,
  not a claim that the release bootstrap succeeded.

## Release prerequisite

The official WebUI nightly moved from pinned commit
`52f9e75b92faf9a23fd150b3c60051c4ec85fc69` to
`f1b28eeeecfc2d63bdb4d4857c6f137468c6d6f6`. The unchanged bootstrap rejects that
move. Advertised official release assets do not expose the five pinned archive
hashes. Do not bypass/relax the guard or treat candidate native reuse as a
release-bootstrap pass. A deliberate upstream adoption or approved immutable
asset recovery is separate release work.

No package publication, release tag or automatic merge is performed by this
patch. Translations tests use the corrected candidates in an isolated source
copy/feed; ordinary dependencies are not overwritten. Its implementation plan
records the final hosted proof and any remaining publication prerequisite.

## Authorized release preparation

The user explicitly approved refreshing the native pins and preparing the
release. Re-read the official nightly metadata, update the archive manifest and
both flake pins together, retain all existing bootstrap guards, and verify all
five official archives plus source/native/package/NativeAOT/lifecycle consumers.
Independent review must check authority, header/ABI compatibility and package
provenance. Retain original verified archives and their upstream metadata for
this release preparation; do not silently substitute historical artifacts when
the official nightly changes.

Translations must repeat its affected isolated Editor build, smoke and full
hosted authoring journey with the new official-native corrected candidate.
The earlier proof with historical native assets remains useful regression
evidence but does not replace this acceptance. Ordinary public dependencies stay
at 4.5 until a corrected public package is available.

Root owns Git integration. Release preparation does not create a tag, publish
registry packages or merge PRs. Final verified release notes and tag/version
preflight will make publication reviewable as a separate step.

Native source preparation is independently reviewed: manifest, flake source
and lock select `f1b28eeeecfc2d63bdb4d4857c6f137468c6d6f6`; all five archives
match the freshly read official API digests and identical normalized source
headers. Verified bootstrap and local Nix checks pass. Source-built library
lifecycle tests ran 6/6 with zero skips. The new upstream CMake project defines
no tests, so the five-platform workflow now runs the maintained lifecycle
filter against its installed source-built library instead of a zero-test
`ctest` invocation. Local evidence is under `artifacts/release-4.6/`.

Prepared versioned release notes: `docs/releases/2.5.0-beta.4.6.md`.
Package, NativeAOT, exact-head platform and fresh official-native Editor
acceptance remain required before publication.
