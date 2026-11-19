using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CsWebUi.Internal;

/// <summary>Reads and sets a WebKitGTK window's placement on GTK's main loop.</summary>
/// <remarks>
/// GTK is not thread-safe, so each call runs on the thread that runs GTK's main loop (the one in
/// <see cref="WebUiApplication.Wait"/>) through <c>g_main_context_invoke</c>, which runs it directly
/// when the caller is that thread or no thread runs the loop. The window pointer is checked
/// against GTK's live top-level windows first, because WebUI can destroy the window meanwhile.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static unsafe class WebUiGtkPlacement
{
    /// <summary>How long a call waits for GTK's main loop.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<Api?> Gtk = new(Api.Load);

    internal static bool TryGet(nint window, out WebUiWindowPlacement placement)
    {
        placement = default;
        var request = new Request(window, null);
        if (!Run(request))
        {
            return false;
        }

        placement = request.Result;
        return true;
    }

    internal static bool TrySet(nint window, WebUiWindowPlacement placement)
        => Run(new Request(window, placement));

    private static bool Run(Request request)
    {
        if (Gtk.Value is not { } api)
        {
            return false;
        }

        var handle = GCHandle.Alloc(request);
        api.MainContextInvoke(null, &RunOnMainLoop, (void*)GCHandle.ToIntPtr(handle));

        // A request the loop never runs keeps its handle; it is small and only happens when the
        // application stopped running GTK's loop.
        return request.Done.Wait(Timeout) && request.Succeeded;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RunOnMainLoop(void* data)
    {
        var handle = GCHandle.FromIntPtr((nint)data);
        var request = (Request)handle.Target!;
        handle.Free();
        try
        {
            request.Succeeded = Gtk.Value is { } api && api.IsLiveWindow(request.Window) && request.Execute(api);
        }
        catch
        {
            // No managed exception may cross GTK's main loop.
            request.Succeeded = false;
        }
        finally
        {
            request.Done.Set();
        }

        return 0; // G_SOURCE_REMOVE
    }

    private sealed class Request(nint window, WebUiWindowPlacement? target)
    {
        internal nint Window { get; } = window;

        internal ManualResetEventSlim Done { get; } = new();

        internal bool Succeeded { get; set; }

        internal WebUiWindowPlacement Result { get; private set; }

        internal bool Execute(Api api)
        {
            var window = (void*)Window;
            var hasPosition = !api.IsWayland();
            if (target is not { } placement)
            {
                int width, height, x = 0, y = 0;
                api.GetSize(window, &width, &height);
                if (hasPosition)
                {
                    api.GetPosition(window, &x, &y);
                }

                Result = new WebUiWindowPlacement(x, y, width, height, api.IsMaximized(window) != 0, hasPosition);
                return true;
            }

            if (placement.Width > 0 && placement.Height > 0)
            {
                // Applies to the restored window even while it is maximized.
                api.Resize(window, placement.Width, placement.Height);
            }

            if (hasPosition && placement.HasPosition)
            {
                api.Move(window, placement.X, placement.Y);
            }

            if (placement.IsMaximized)
            {
                api.Maximize(window);
            }
            else
            {
                api.Unmaximize(window);
            }

            return true;
        }
    }

    private sealed class Api
    {
        internal delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int>, void*, void> MainContextInvoke;
        internal delegate* unmanaged[Cdecl]<void*, int*, int*, void> GetSize;
        internal delegate* unmanaged[Cdecl]<void*, int*, int*, void> GetPosition;
        internal delegate* unmanaged[Cdecl]<void*, int> IsMaximized;
        internal delegate* unmanaged[Cdecl]<void*, int, int, void> Resize;
        internal delegate* unmanaged[Cdecl]<void*, int, int, void> Move;
        internal delegate* unmanaged[Cdecl]<void*, void> Maximize;
        internal delegate* unmanaged[Cdecl]<void*, void> Unmaximize;
        private delegate* unmanaged[Cdecl]<void*> _listToplevels;
        private delegate* unmanaged[Cdecl]<void*, void> _listFree;
        private delegate* unmanaged[Cdecl]<void*> _displayGetDefault;
        private delegate* unmanaged[Cdecl]<nuint> _waylandDisplayGetType;
        private delegate* unmanaged[Cdecl]<void*, nuint, int> _typeCheckInstanceIsA;

        // WebUI loads GTK 3 under this name; dlsym on its handle also finds GLib and GDK, which
        // it links.
        internal static Api? Load()
        {
            if (!NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk))
            {
                return null;
            }

            nint Export(string name) => NativeLibrary.TryGetExport(gtk, name, out var address) ? address : 0;
            var api = new Api
            {
                MainContextInvoke = (delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, int>, void*, void>)Export("g_main_context_invoke"),
                GetSize = (delegate* unmanaged[Cdecl]<void*, int*, int*, void>)Export("gtk_window_get_size"),
                GetPosition = (delegate* unmanaged[Cdecl]<void*, int*, int*, void>)Export("gtk_window_get_position"),
                IsMaximized = (delegate* unmanaged[Cdecl]<void*, int>)Export("gtk_window_is_maximized"),
                Resize = (delegate* unmanaged[Cdecl]<void*, int, int, void>)Export("gtk_window_resize"),
                Move = (delegate* unmanaged[Cdecl]<void*, int, int, void>)Export("gtk_window_move"),
                Maximize = (delegate* unmanaged[Cdecl]<void*, void>)Export("gtk_window_maximize"),
                Unmaximize = (delegate* unmanaged[Cdecl]<void*, void>)Export("gtk_window_unmaximize"),
                _listToplevels = (delegate* unmanaged[Cdecl]<void*>)Export("gtk_window_list_toplevels"),
                _listFree = (delegate* unmanaged[Cdecl]<void*, void>)Export("g_list_free"),
                _displayGetDefault = (delegate* unmanaged[Cdecl]<void*>)Export("gdk_display_get_default"),
                _waylandDisplayGetType = (delegate* unmanaged[Cdecl]<nuint>)Export("gdk_wayland_display_get_type"),
                _typeCheckInstanceIsA = (delegate* unmanaged[Cdecl]<void*, nuint, int>)Export("g_type_check_instance_is_a"),
            };

            return api.MainContextInvoke is null || api.GetSize is null || api.GetPosition is null
                || api.IsMaximized is null || api.Resize is null || api.Move is null || api.Maximize is null
                || api.Unmaximize is null || api._listToplevels is null || api._listFree is null
                || api._displayGetDefault is null || api._typeCheckInstanceIsA is null
                ? null
                : api;
        }

        internal bool IsLiveWindow(nint window)
        {
            var list = (GList*)_listToplevels();
            try
            {
                for (var item = list; item is not null; item = item->Next)
                {
                    if ((nint)item->Data == window)
                    {
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                _listFree(list);
            }
        }

        // GDK built without Wayland has no gdk_wayland_display_get_type, and then runs on X11.
        internal bool IsWayland()
        {
            var display = _displayGetDefault();
            return _waylandDisplayGetType is not null && display is not null
                && _typeCheckInstanceIsA(display, _waylandDisplayGetType()) != 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public void* Data;
        public GList* Next;
        public GList* Previous;
    }
}
