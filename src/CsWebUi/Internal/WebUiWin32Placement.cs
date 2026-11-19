using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CsWebUi.Internal;

/// <summary>Reads and sets a WebView2 window's placement with Win32 directly.</summary>
/// <remarks>
/// WebUI's own <c>webui_set_position</c> and <c>webui_set_size</c> take unsigned values and refuse
/// positions outside 0..3000 × 0..1800 and sizes outside 100..3840, and WebUI has no getters.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WebUiWin32Placement
{
    private const uint SwShowMinimized = 2;
    private const uint SwShowMaximized = 3;
    private const int SwMaximize = 3;
    private const int SwRestore = 9;
    private const uint WpfRestoreToMaximized = 2;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Whether the handle is a live window of this process, as a WebView2 window is.</summary>
    internal static bool IsOwnWindow(nint window)
    {
        if (window == 0 || IsWindow(window) == 0)
        {
            return false;
        }

        uint processId = 0;
        return GetWindowThreadProcessId(window, &processId) != 0 && processId == (uint)Environment.ProcessId;
    }

    internal static bool TryGet(nint window, out WebUiWindowPlacement placement)
    {
        placement = default;
        var stored = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        if (GetWindowPlacement(window, &stored) == 0)
        {
            return false;
        }

        var maximized = stored.ShowCommand == SwShowMaximized
            || (stored.ShowCommand == SwShowMinimized && (stored.Flags & WpfRestoreToMaximized) != 0);
        Rect bounds;
        if (IsZoomed(window) == 0 && IsIconic(window) == 0)
        {
            // A restored window: its screen rectangle is exact.
            if (GetWindowRect(window, &bounds) == 0)
            {
                return false;
            }
        }
        else
        {
            // rcNormalPosition is in workspace coordinates, which start at the work area of the
            // window's monitor; move them into screen coordinates.
            bounds = stored.NormalPosition;
            if (!TryGetWorkAreaOffset(MonitorFromWindow(window, MonitorDefaultToNearest), out var dx, out var dy))
            {
                return false;
            }

            bounds.Left += dx;
            bounds.Right += dx;
            bounds.Top += dy;
            bounds.Bottom += dy;
        }

        placement = new WebUiWindowPlacement(
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            maximized);
        return true;
    }

    internal static bool TrySet(nint window, WebUiWindowPlacement placement)
    {
        // Restore first, so the bounds below are the restored bounds, then maximize on the monitor
        // those bounds lie on.
        if (IsZoomed(window) != 0 || IsIconic(window) != 0)
        {
            // ShowWindow returns the previous visibility, not an error.
            _ = ShowWindow(window, SwRestore);
        }

        if (SetWindowPos(
                window,
                0,
                placement.X,
                placement.Y,
                placement.Width,
                placement.Height,
                SwpNoZOrder | SwpNoActivate) == 0)
        {
            return false;
        }

        if (placement.IsMaximized)
        {
            _ = ShowWindow(window, SwMaximize);
        }

        return true;
    }

    private static bool TryGetWorkAreaOffset(nint monitor, out int dx, out int dy)
    {
        dx = dy = 0;
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (monitor == 0 || GetMonitorInfoW(monitor, &info) == 0)
        {
            return false;
        }

        dx = info.Work.Left - info.Monitor.Left;
        dy = info.Work.Top - info.Monitor.Top;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial int IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial int IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    private static partial int IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, uint* processId);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowPlacement(nint window, WindowPlacement* placement);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowRect(nint window, Rect* rect);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfo* info);
}
