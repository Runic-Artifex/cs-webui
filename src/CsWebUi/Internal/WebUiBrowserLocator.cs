namespace CsWebUi.Internal;

/// <summary>
/// Mirrors how WebUI finds a browser executable, so failures can name what was tried and a
/// configured browser path can be mapped onto WebUI's browser folder and browser kind.
/// </summary>
/// <remarks>
/// The tables follow <c>_webui_custom_browser_exist</c> and <c>_webui_browser_exist</c> in
/// <c>webui.c</c> at the pinned revision. WebUI first looks in the folder set by
/// <c>webui_set_browser_folder</c> for the names in <see cref="GetFolderNames"/>, then on Linux runs
/// <c>&lt;name&gt; --version</c> through the shell (so <c>PATH</c> decides), on Windows reads the
/// registry <c>App Paths</c> entry, and on macOS asks Launch Services for the application.
/// </remarks>
internal static class WebUiBrowserLocator
{
    private static readonly WebUiBrowser[] Concrete =
    [
        WebUiBrowser.Chrome,
        WebUiBrowser.Edge,
        WebUiBrowser.Epic,
        WebUiBrowser.Vivaldi,
        WebUiBrowser.Brave,
        WebUiBrowser.Firefox,
        WebUiBrowser.Yandex,
        WebUiBrowser.Chromium,
    ];

    internal static string DisplayName(WebUiBrowser browser) => browser switch
    {
        WebUiBrowser.Chrome => "Google Chrome",
        WebUiBrowser.Edge => "Microsoft Edge",
        WebUiBrowser.Epic => "Epic",
        WebUiBrowser.Vivaldi => "Vivaldi",
        WebUiBrowser.Brave => "Brave",
        WebUiBrowser.Firefox => "Firefox",
        WebUiBrowser.Yandex => "Yandex",
        WebUiBrowser.Chromium => "Chromium",
        WebUiBrowser.ChromiumBased => "a Chromium-based browser",
        WebUiBrowser.WebView => "the embedded WebView",
        WebUiBrowser.AnyBrowser => "any supported browser",
        _ => browser.ToString(),
    };

    /// <summary>File names WebUI accepts for a browser in a custom browser folder.</summary>
    internal static IReadOnlyList<string> GetFolderNames(WebUiBrowser browser)
    {
        if (OperatingSystem.IsWindows())
        {
            return browser switch
            {
                WebUiBrowser.Chrome or WebUiBrowser.Chromium => ["chrome.exe"],
                WebUiBrowser.Edge => ["msedge.exe"],
                WebUiBrowser.Epic => ["epic.exe"],
                WebUiBrowser.Vivaldi => ["vivaldi.exe"],
                WebUiBrowser.Brave => ["brave.exe"],
                WebUiBrowser.Firefox => ["firefox.exe"],
                WebUiBrowser.Yandex => ["browser.exe"],
                _ => [],
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return browser switch
            {
                WebUiBrowser.Chrome => ["Google Chrome.app"],
                WebUiBrowser.Edge => ["Microsoft Edge.app"],
                WebUiBrowser.Epic => ["Epic.app"],
                WebUiBrowser.Vivaldi => ["Vivaldi.app"],
                WebUiBrowser.Brave => ["Brave Browser.app"],
                WebUiBrowser.Firefox => ["Firefox.app"],
                WebUiBrowser.Yandex => ["Yandex.app"],
                WebUiBrowser.Chromium => ["Chromium.app"],
                _ => [],
            };
        }

        return browser switch
        {
            WebUiBrowser.Chrome => ["google-chrome", "google-chrome-stable"],
            WebUiBrowser.Edge => ["microsoft-edge-stable", "microsoft-edge-beta"],
            WebUiBrowser.Epic => ["epic"],
            WebUiBrowser.Vivaldi => ["vivaldi", "vivaldi-stable"],
            WebUiBrowser.Brave => ["brave", "brave-browser-stable", "brave-browser-nightly", "brave-browser-beta"],
            WebUiBrowser.Firefox => ["firefox"],
            WebUiBrowser.Yandex => ["yandex-browser"],
            WebUiBrowser.Chromium => ["chromium-browser", "chromium"],
            _ => [],
        };
    }

    /// <summary>Command names WebUI probes on Linux when no custom folder provides the browser.</summary>
    private static IReadOnlyList<string> GetLinuxCommandNames(WebUiBrowser browser) => browser switch
    {
        WebUiBrowser.Edge => ["microsoft-edge-stable", "microsoft-edge-beta", "microsoft-edge-dev"],
        WebUiBrowser.Vivaldi => ["vivaldi", "vivaldi-stable", "vivaldi-snapshot"],
        _ => GetFolderNames(browser),
    };

    /// <summary>Identifies the browser kind WebUI would launch for an executable or app bundle path.</summary>
    internal static WebUiBrowser? Identify(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        foreach (var browser in Concrete)
        {
            if (GetFolderNames(browser).Contains(name, OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
            {
                return browser;
            }
        }

        return null;
    }

    /// <summary>All file names accepted by <see cref="Identify"/> on this platform.</summary>
    internal static IEnumerable<string> AllFolderNames() => Concrete.SelectMany(GetFolderNames).Distinct();

    /// <summary>Finds the executable WebUI most likely uses for a browser, or a description when it is not a file.</summary>
    internal static string? Find(WebUiBrowser browser, string? browserFolder)
    {
        if (!string.IsNullOrEmpty(browserFolder) && Directory.Exists(browserFolder))
        {
            foreach (var name in GetFolderNames(browser))
            {
                var candidate = Path.Combine(browserFolder, name);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return FindInAppPaths(browser);
            }

            if (OperatingSystem.IsMacOS())
            {
                return GetFolderNames(browser) is [var app]
                    ? $"{app} (found by macOS Launch Services)"
                    : null;
            }

            return FindOnPath(GetLinuxCommandNames(browser));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? FindOnPath(IReadOnlyList<string> names)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var name in names)
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindInAppPaths(WebUiBrowser browser)
    {
        if (GetFolderNames(browser) is not [var executable])
        {
            return null;
        }

        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executable}";
        foreach (var root in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
        {
            using var entry = root.OpenSubKey(key);
            if (entry?.GetValue(null) is string value && value.Length > 0)
            {
                return value.Trim('"');
            }
        }

        return null;
    }
}
