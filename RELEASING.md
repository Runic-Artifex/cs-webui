# Releasing CsWebUi

CsWebUi already publishes its two NuGet packages through the `NuGet Gallery`
workflow when a GitHub release is published. Its nuget.org trusted-publisher
policy is bound to `.github/workflows/nuget-gallery.yml` and the `nuget`
environment; keep that workflow identity until the registry policy is migrated
deliberately.

Before publishing a release, verify that the release tag exactly matches the
project package version, all package and NativeAOT checks pass, and the `nuget`
environment variable `NUGET_USER` names the nuget.org account. CsWebUi can keep
releasing independently of the Runic Toolkit package family.
