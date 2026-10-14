using System.Diagnostics;
using System.Runtime.InteropServices;
using CsWebUi.Native;

namespace CsWebUi.Internal;

/// <summary>
/// Guards native event access against WebUI abandoning pending events when the application exits.
/// </summary>
/// <remarks>
/// <para>
/// WebUI handles each WebSocket message on its own thread and passes the callback a
/// <c>webui_event_t</c> that lives on that thread's stack. With asynchronous responses enabled
/// the thread then waits for the reply, but <c>webui_exit()</c> (also called by
/// <c>webui_clean()</c>) makes it stop waiting: it sends an empty reply, frees the event and the
/// thread ends. Any later <c>webui_return_*</c> or event accessor reads a dead stack frame and
/// freed event memory (runic-sdk#239).
/// </para>
/// <para>
/// Access from the thread that is still running the native callback is safe until WebUI frees its
/// state. Every other access enters this gate. Exit and clean close the gate and wait for the
/// accesses already inside it before calling native code, so no access can overlap the moment WebUI
/// abandons its events. Clean additionally waits for callbacks still running on other threads,
/// because <c>webui_clean()</c> frees all native memory about two seconds after it exits. A clean
/// starts a new generation; events and windows from an earlier generation never touch native state
/// again.
/// </para>
/// <para>
/// Both waits are bounded by <see cref="WaitLimit"/>: a gated call such as
/// <c>webui_show_client</c> can block for up to WebUI's 60-second connection timeout, and a
/// callback can block for as long as the application's code does. When the limit passes, the
/// transition proceeds and a warning is traced; an access still in progress at that point is not
/// protected. WebUI can also call <c>webui_exit()</c> itself, from <c>_webui_panic</c> after an
/// internal failure such as an allocation failure; that exit bypasses this gate.
/// </para>
/// <para>
/// Since 4e65ad711 (webui-dev/webui#720 and #721), WebUI keeps an event it stopped waiting for,
/// ignores a later reply to it, and assigns event slots under its lock. A late reply after an exit
/// alone, including one from <c>_webui_panic</c>, is then safe in WebUI itself. The gate is still
/// required for <c>webui_clean()</c>: it frees every window and event while a reply can be in
/// flight, and clears the flag that late replies check only after the memory is gone. It also
/// keeps the managed contract that a released event throws <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
internal static class WebUiNativeLifetime
{
    /// <summary>WebUI's longest connection timeout (WEBUI_MAX_TIMEOUT) plus a margin.</summary>
    internal static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(75);

    private static readonly object TransitionGate = new();
    private static readonly List<nint> RetainedBuffers = [];
    private static int _generation;
    private static int _closed;
    private static int _accessors;
    private static int _dispatches;

    internal static int Generation => Volatile.Read(ref _generation);

    /// <summary>Gets whether Exit or Clean has stopped native event access in the current generation.</summary>
    internal static bool IsExiting => Volatile.Read(ref _closed) != 0;

    /// <summary>Enters the gate when native state from <paramref name="generation"/> is still live.</summary>
    internal static bool TryEnter(int generation)
    {
        // Interlocked operations are full fences on both sides (a Dekker handshake with Close):
        // either Close observes this accessor and waits for it, or this accessor observes the
        // closed gate and stays out.
        Interlocked.Increment(ref _accessors);
        if (Volatile.Read(ref _closed) == 0 && Volatile.Read(ref _generation) == generation)
        {
            return true;
        }

        Interlocked.Decrement(ref _accessors);
        return false;
    }

    internal static void Leave() => Interlocked.Decrement(ref _accessors);

    /// <summary>Counts a managed callback that runs inside a native WebUI callback.</summary>
    internal static void EnterDispatch() => Interlocked.Increment(ref _dispatches);

    internal static void LeaveDispatch() => Interlocked.Decrement(ref _dispatches);

    /// <summary>Creates a native window and returns it with its generation, atomically with Clean.</summary>
    internal static nuint CreateWindow(Func<nuint> create, out int generation)
    {
        lock (TransitionGate)
        {
            generation = Volatile.Read(ref _generation);
            return create();
        }
    }

    /// <summary>Gets how many native buffers wait for the next clean.</summary>
    internal static int RetainedBufferCount
    {
        get
        {
            lock (RetainedBuffers)
            {
                return RetainedBuffers.Count;
            }
        }
    }

    /// <summary>Keeps a native buffer WebUI may still write into until <c>webui_clean()</c> has returned.</summary>
    /// <remarks>
    /// Before 2456744e6, <c>webui_script()</c> left a timed-out call's buffer registered and copied a
    /// late result into it. Only <c>webui_clean()</c> dropped that registration: it frees WebUI's
    /// state, and the next initialization clears it. Since 2456744e6 (webui-dev/webui#722) WebUI
    /// releases the buffer under its lock before <c>webui_script()</c> returns and discards a late
    /// result, so with that WebUI the kept buffer is never written; it is kept for older builds.
    /// </remarks>
    internal static unsafe void RetainUntilClean(void* buffer)
    {
        lock (RetainedBuffers)
        {
            RetainedBuffers.Add((nint)buffer);
        }
    }

    /// <summary>Stops native event access, then asks WebUI to exit.</summary>
    internal static void Exit()
    {
        lock (TransitionGate)
        {
            Close();
            WebUiNative.Exit();
        }
    }

    /// <summary>Destroys a native window unless WebUI was cleaned since it was created.</summary>
    /// <remarks>Serialized with <see cref="Clean"/>, so a deferred destroy never runs while WebUI frees its state.</remarks>
    internal static void Destroy(nuint window, int generation)
    {
        lock (TransitionGate)
        {
            // After a clean the native window no longer exists, and its id may already belong
            // to a window of the new generation.
            if (generation == Volatile.Read(ref _generation))
            {
                WebUiNative.Destroy(window);
            }
        }
    }

    /// <summary>Stops native event access, releases WebUI's native state and starts a new generation.</summary>
    /// <param name="insideCallback">
    /// The caller is running inside a native callback. WebUI then only exits; the current
    /// generation stays closed until a later clean from outside a callback.
    /// </param>
    internal static void Clean(bool insideCallback)
    {
        lock (TransitionGate)
        {
            Close();
            if (insideCallback)
            {
                // Freeing native state now would pull it from under the running callback.
                // webui_clean() itself only exits inside a binding callback, but not inside a
                // file-handler callback, so exit explicitly in both cases.
                WebUiNative.Exit();
                return;
            }

            // Exit first so WebUI starts no new callbacks.
            WebUiNative.Exit();
        }

        // Let the callbacks already running on other threads leave native code before
        // webui_clean() frees it. This waits outside the lock, so a callback that calls Exit
        // or Clean itself does not block on it.
        WaitUntilZero(ref _dispatches, "managed callbacks still running");

        lock (TransitionGate)
        {
            WebUiNative.Clean();
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _closed, 0);
        }

        ReleaseRetainedBuffers();
    }

    private static unsafe void ReleaseRetainedBuffers()
    {
        lock (RetainedBuffers)
        {
            foreach (var buffer in RetainedBuffers)
            {
                NativeMemory.Free((void*)buffer);
            }

            RetainedBuffers.Clear();
        }
    }

    private static void Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        WaitUntilZero(ref _accessors, "native event calls still in progress");
    }

    private static void WaitUntilZero(ref int counter, string what)
    {
        if (Volatile.Read(ref counter) == 0)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var spinner = default(SpinWait);
        while (Volatile.Read(ref counter) != 0)
        {
            if (Stopwatch.GetElapsedTime(started) > WaitLimit)
            {
                Trace.TraceWarning(
                    $"CsWebUi: WebUI exit or clean proceeds after waiting {WaitLimit.TotalSeconds:0} s for {Volatile.Read(ref counter)} {what}; they are no longer protected.");
                return;
            }

            spinner.SpinOnce();
        }
    }
}
