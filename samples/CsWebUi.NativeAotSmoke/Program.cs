using CsWebUi;
using CsWebUi.Native;

var libraryPath = Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY");
if (string.IsNullOrWhiteSpace(libraryPath))
{
    Console.Error.WriteLine("CSWEBUI_NATIVE_LIBRARY must point to a WebUI shared library.");
    return 2;
}

WebUiNativeLibrary.SetLibraryPath(libraryPath);
var port = WebUiNative.GetFreePort();
using var window = new WebUiWindow();
window.SetFileHandler(static _ => WebUiFileHandlerResult.FromResponse(
    "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"u8.ToArray()));

Console.WriteLine($"WebUI allocated port {port}.");
return port == 0 || window.Id == 0 ? 1 : 0;
