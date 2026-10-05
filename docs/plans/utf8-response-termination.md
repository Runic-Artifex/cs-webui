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
