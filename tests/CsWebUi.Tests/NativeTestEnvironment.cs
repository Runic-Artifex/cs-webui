using System.Runtime.CompilerServices;
using CsWebUi.Native;

namespace CsWebUi.Tests;

internal static class NativeTestEnvironment
{
    internal const string MissingLibraryReason =
        "CSWEBUI_NATIVE_LIBRARY is not configured; native smoke tests require a built WebUI library.";

    [ModuleInitializer]
    internal static void ConfigureLibraryPath()
    {
        var libraryPath = Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY");
        if (!string.IsNullOrWhiteSpace(libraryPath))
        {
            WebUiNativeLibrary.SetLibraryPath(libraryPath);
        }
    }
}

internal sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY")))
        {
            Skip = NativeTestEnvironment.MissingLibraryReason;
        }
    }
}

internal sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY")))
        {
            Skip = NativeTestEnvironment.MissingLibraryReason;
        }
    }
}
