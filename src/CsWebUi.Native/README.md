# CS-WebUI Native

Use the complete WebUI C ABI from .NET when you need exact native control.
`CsWebUi.Native` provides NativeAOT-compatible, allocation-free
`LibraryImport` bindings while retaining the C API's pointer, lifetime, and
callback responsibilities.

## Install

```bash
dotnet add package CsWebUi.Native --prerelease
```

`CsWebUi.Native` targets .NET 10 and is currently a prerelease package
following the WebUI 2.5 beta ABI. Release packages include verified official
WebUI native libraries for `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`,
and `osx-arm64`.

Browser mode needs a supported installed browser. Embedded-WebView mode needs
WebView2 on Windows, WebKitGTK on Linux, or WebKit on macOS. Your project must
enable unsafe code because the ABI exposes pointers and unmanaged function
pointers:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

## First window

```csharp
using System;
using CsWebUi.Native;

unsafe
{
    var window = WebUiNative.NewWindow();
    ReadOnlySpan<byte> page = "<h1>Hello from CS-WebUI Native</h1>\0"u8;

    try
    {
        fixed (byte* content = page)
        {
            if (WebUiNative.Show(window, content) == 0)
            {
                throw new InvalidOperationException("WebUI could not show the page.");
            }
        }

        WebUiNative.Wait();
    }
    finally
    {
        WebUiNative.Destroy(window);
    }
}
```

WebUI owns pointers it returns unless the upstream API explicitly requires
`WebUiNative.Free`. Keep callbacks and any memory passed across the ABI valid
for the lifetime WebUI requires; this package intentionally does not add the
ownership and UTF-8 safety layer.

## Choose this package

Use `CsWebUi.Native` for direct C-ABI integrations, custom callback/function
pointer handling, or when a NativeAOT-friendly low-level binding is required.
For window ownership, managed event arguments, synchronous or asynchronous
callbacks, and high-level file handling, install
[`CsWebUi`](https://www.nuget.org/packages/CsWebUi) instead.

For Windows `win-x64` NativeAOT publishing, `CsWebUiStaticLink=true` links the
WebUI and WebView2 loader libraries into the executable. It is opt-in, only
supports `win-x64`, and bypasses `CSWEBUI_NATIVE_LIBRARY` and
`WebUiNativeLibrary.SetLibraryPath`. Otherwise, set a custom native library
path before the first `WebUiNative` call when overriding the bundled asset.

## Learn and get help

- [Repository documentation](https://github.com/Runic-Artifex/cs-webui#readme)
- [NativeAOT smoke example](https://github.com/Runic-Artifex/cs-webui/tree/main/samples/CsWebUi.NativeAotSmoke)
- [WebUI C API documentation](https://webui.me/docs/)
- [Report an issue or request support](https://github.com/Runic-Artifex/cs-webui/issues)

CS-WebUI is MIT licensed. See the
[license](https://github.com/Runic-Artifex/cs-webui/blob/main/LICENSE) and
[WebUI notices](https://github.com/Runic-Artifex/cs-webui/tree/main/eng/licenses)
for bundled-component terms.
