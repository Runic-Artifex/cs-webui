using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsWebUi;

var payloadBytes = ReadPayloadSize();
var payload = Enumerable.Repeat((byte)'R', payloadBytes).ToArray();
var headers = Encoding.ASCII.GetBytes(
    "HTTP/1.1 200 OK\r\n" +
    "Content-Type: application/octet-stream\r\n" +
    $"Content-Length: {payload.Length}\r\n" +
    "Cache-Control: no-store\r\n" +
    "Connection: close\r\n\r\n");
var response = new byte[headers.Length + payload.Length];
headers.CopyTo(response, 0);
payload.CopyTo(response, headers.Length);
var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

using var window = new WebUiWindow();
window.SetFileHandler(_ => WebUiFileHandlerResult.FromResponse(response));
var baseUrl = new Uri(window.StartServer("<html></html>"));

Write(new
{
    schema = "runic.comparative-stress-adapter-ready/1",
    implementation = "cs-webui",
    revision = Environment.GetEnvironmentVariable("RUNIC_STRESS_IMPLEMENTATION_REVISION"),
    url = new Uri(baseUrl, "payload").AbsoluteUri,
    runtime = RuntimeInformation.FrameworkDescription,
    adapterAssemblySha256 = Hash(Assembly.GetExecutingAssembly().Location),
    implementationAssemblySha256 = Hash(typeof(WebUiWindow).Assembly.Location),
    nativeLibrarySha256 = Hash(Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY")
        ?? throw new InvalidOperationException("CSWEBUI_NATIVE_LIBRARY is required.")),
    shutdownBoundary = "managed-disposal-after-server-only-stress",
});

if (!string.Equals(await Console.In.ReadLineAsync(), "stop", StringComparison.Ordinal))
{
    return 2;
}

using var process = Process.GetCurrentProcess();
Write(new
{
    schema = "runic.comparative-stress-adapter-stopped/1",
    implementation = "cs-webui",
    managedAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
    peakWorkingSetBytes = process.PeakWorkingSet64,
});
return 0;

static int ReadPayloadSize()
{
    var value = Environment.GetEnvironmentVariable("RUNIC_STRESS_PAYLOAD_BYTES");
    return int.TryParse(value, out var result) && result is > 0 and <= 1_048_576
        ? result
        : throw new InvalidOperationException("RUNIC_STRESS_PAYLOAD_BYTES must be between 1 and 1048576.");
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

static void Write<T>(T value)
{
    Console.WriteLine(JsonSerializer.Serialize(value));
    Console.Out.Flush();
}
