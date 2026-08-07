# CS-WebUI High-Level Sample

This sample is a complete small application built only with the `CsWebUi`
package's high-level API. It serves a local HTML/CSS/JavaScript application
through WebUI and demonstrates:

- deterministic `WebUiWindow` ownership and cleanup;
- synchronous typed callbacks and `WebUiResult` responses;
- `ValueTask`-based asynchronous callbacks;
- JavaScript execution initiated by the .NET backend;
- raw binary messages from JavaScript to .NET and back;
- window configuration, backend logging, browser launching, and clean shutdown.

Run it from the repository root:

```bash
nix develop
dotnet run --project samples/CsWebUi.HighLevelSample
```

It needs a supported browser installed. On NixOS, the flake's development shell provides Chromium for the sample.

The sample deliberately keeps DOM element IDs separate from backend binding names. WebUI can automatically forward matching element clicks as zero-argument events; explicit `webui.call("bindingName", ...args)` is the right pattern for typed function calls.
