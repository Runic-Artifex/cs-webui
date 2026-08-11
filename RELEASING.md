# Releasing CS-WebUI

CS-WebUI already publishes its two NuGet packages through the `NuGet Gallery`
workflow when a GitHub release is published. Its nuget.org trusted-publisher
policy is bound to `.github/workflows/nuget-gallery.yml` and the `nuget`
environment; keep that workflow identity until the registry policy is migrated
deliberately.

Before publishing a release, verify that the release tag exactly matches the
project package version, all package and NativeAOT checks pass, and the `nuget`
environment variable `NUGET_USER` names the nuget.org account. CS-WebUI can keep
releasing independently of the Runic Toolkit package family.

## Updating the official WebUI nightly

WebUI's `nightly` release tag and assets are mutable. CS-WebUI therefore pins
the release's source commit and every archive SHA-256 in
`eng/webui-nightly-assets.json`. The bootstrap fails closed when the upstream
release moves; never weaken or skip that check to make a release pass.

To adopt a newer nightly deliberately:

1. Read the commit from the official release body and the `sha256:` digests
   returned by GitHub's release-assets API.
2. Update the commit, version, archive names, and digests in
   `eng/webui-nightly-assets.json`.
3. Update `CsWebUi.Native` for any ABI additions until
   `eng/validate-webui-abi.sh` passes against the downloaded header.
4. Deliberately update the independently tested source revision in `flake.nix`
   when the fork or upstream source used for development should advance, then
   run `nix flake update webui --accept-flake-config`. It does not need to match
   the official binary revision, but its exported ABI must remain compatible.
5. Run the verified bootstrap and both verification paths:

   ```bash
   output="$(mktemp -d)"
   nix develop . -c ./eng/bootstrap-webui.sh "$output"
   nix flake check --accept-flake-config
   nix develop . -c env \
     CSWEBUI_NATIVE_LIBRARY="$output/native/linux-x64/libwebui-2.so" \
     dotnet test CsWebUi.sln --configuration Release
   ```

The NuGet workflow packages only the bootstrapped official archives. Its
separate source-build matrix and lifecycle regression must also succeed for the
test-source revision pinned in `flake.lock`.
