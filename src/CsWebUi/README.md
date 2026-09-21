# CS-WebUI

Build lightweight, cross-platform .NET desktop interfaces with HTML, CSS, and
JavaScript. `CsWebUi` manages WebUI windows and callbacks so your application
can use an installed browser or a supported WebView without taking on a native
interop surface.

## Install

```bash
dotnet add package CsWebUi --prerelease
```

`CsWebUi` targets .NET 10 and brings in `CsWebUi.Native` automatically. It is
currently a prerelease package following the WebUI 2.5 beta ABI. Release
packages include verified official WebUI native libraries for `win-x64`,
`linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

Browser mode needs a supported installed browser. Embedded-WebView mode needs
the platform runtime: WebView2 on Windows, WebKitGTK on Linux, or WebKit on
macOS.

## First window

```csharp
using CsWebUi;

using var window = new WebUiWindow();

window.Bind("multiply", static e =>
    WebUiResult.FromInt64(e.GetInt64() * e.GetInt64(1)));

window.Show("""
    <!doctype html>
    <script src="webui.js"></script>
    <button onclick="multiply(6, 7).then(alert)">Multiply</button>
    """);

WebUiApplication.Wait();
```

`WebUiWindow.Dispose()` destroys the native window after active managed
callbacks finish. Use `BindAsync` when a JavaScript call needs an asynchronous
`ValueTask<WebUiResult>` response.

## Choose this package

Use `CsWebUi` for application code. It is the ergonomic layer for windows,
events, callbacks, JavaScript evaluation, browser and server configuration,
custom file responses, browser selection, event blocking, frameless or
transparent WebViews, custom browser parameters, and in-memory icons. Use
[`CsWebUi.Native`](https://www.nuget.org/packages/CsWebUi.Native) only when you
need direct, unsafe access to the full WebUI C ABI.

For Windows `win-x64` NativeAOT publishing, set `CsWebUiStaticLink` to `true`
alongside `PublishAot`; static linking is opt-in and bypasses dynamic native
library overrides. For all other deployments, the normal bundled native asset
is used. To load a custom native library, call
`CsWebUi.Native.WebUiNativeLibrary.SetLibraryPath` before the first WebUI call,
or set `CSWEBUI_NATIVE_LIBRARY`.

## Learn and get help

- [Repository documentation](https://github.com/Runic-Artifex/cs-webui#readme)
- [Basic callback example](https://github.com/Runic-Artifex/cs-webui/tree/main/samples/CsWebUi.BasicSample)
- [High-level API example](https://github.com/Runic-Artifex/cs-webui/tree/main/samples/CsWebUi.HighLevelSample)
- [WebUI documentation](https://webui.me/docs/)
- [Report an issue or request support](https://github.com/Runic-Artifex/cs-webui/issues)

CS-WebUI is MIT licensed. See the
[license](https://github.com/Runic-Artifex/cs-webui/blob/main/LICENSE) and
[WebUI notices](https://github.com/Runic-Artifex/cs-webui/tree/main/eng/licenses)
for bundled-component terms.
