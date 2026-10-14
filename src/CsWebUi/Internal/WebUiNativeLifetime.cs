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
/// Access from the thread that is still running the native callback is always safe. Every other
/// access enters this gate. Exit and clean close the gate and wait for the accesses already inside
/// it before calling native code, so no access can overlap the moment WebUI abandons its events.
/// A clean starts a new generation; events and windows from an earlier generation never touch
/// native state again.
/// </para>
/// </remarks>
internal static class WebUiNativeLifetime
{
    private static readonly object TransitionGate = new();
    private static int _generation;
    private static int _closed;
    private static int _accessors;

    internal static int Generation => Volatile.Read(ref _generation);

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
    /// The caller is running inside a native callback. WebUI then only exits and defers its
    /// cleanup, so the current generation stays closed until a later clean.
    /// </param>
    internal static void Clean(bool insideCallback)
    {
        lock (TransitionGate)
        {
            Close();
            WebUiNative.Clean();
            if (insideCallback)
            {
                return;
            }

            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _closed, 0);
        }
    }

    private static void Close()
    {
        Interlocked.Exchange(ref _closed, 1);
        var spinner = default(SpinWait);
        while (Volatile.Read(ref _accessors) != 0)
        {
            spinner.SpinOnce();
        }
    }
}
