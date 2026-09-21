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

WebUI's `nightly` release tag and assets are mutable. The official release
authority for CS-WebUI is therefore `eng/webui-nightly-assets.json`, which pins
the `webui-dev/webui` repository, source commit, version, archive names, and
every archive SHA-256. The bootstrap fails closed when the upstream release
moves; never weaken or skip that check to make a release pass. The `webui`
entry in `flake.lock` must pin the same official repository and exact commit.

To adopt a newer nightly deliberately:

1. Read the commit from the official release body and the `sha256:` digests
   returned by GitHub's release-assets API.
2. Update the commit, version, archive names, and digests in
   `eng/webui-nightly-assets.json`.
3. Update `CsWebUi.Native` for any ABI additions until
   `eng/validate-webui-abi.sh` passes against the downloaded header.
4. Update the official source revision in `flake.nix`, then run
   `nix flake update webui --accept-flake-config`. Confirm the resulting
   `flake.lock` repository and 40-character revision exactly match the manifest.
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
separate source-build matrix, native lifecycle regression, and package-consumer
server lifecycle must also succeed for the same official revision pinned in
`flake.lock`.
