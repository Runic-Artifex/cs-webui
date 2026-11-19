namespace CsWebUi;

/// <summary>The restored bounds and the maximized state of an embedded WebView window.</summary>
/// <remarks>
/// <para>
/// The bounds are the window's restored (not maximized) bounds in pixels, so a placement read when
/// the window closes restores the same window at the next start. The position is signed: a window
/// on a monitor left of or above the primary one has a negative position.
/// </para>
/// <para>
/// The units are those the platform uses for <see cref="WebUiWindow.SetSize"/> and
/// <see cref="WebUiWindow.SetPosition"/>. On Windows the bounds are the outer window rectangle in
/// screen coordinates, scaled as the process's DPI awareness scales them. On Linux the size is
/// GTK's window size (without decorations the window manager draws), and the position is the
/// window frame's on X11.
/// </para>
/// </remarks>
/// <param name="X">The left edge of the restored window.</param>
/// <param name="Y">The top edge of the restored window.</param>
/// <param name="Width">The width of the restored window.</param>
/// <param name="Height">The height of the restored window.</param>
/// <param name="IsMaximized">Whether the window is maximized.</param>
/// <param name="HasPosition">
/// Whether <paramref name="X"/> and <paramref name="Y"/> are meaningful. Wayland tells
/// applications nothing about the position of their windows and does not let them choose it, so a
/// GTK window on Wayland reports <see langword="false"/>, and a position given to it is ignored.
/// </param>
public readonly record struct WebUiWindowPlacement(
    int X,
    int Y,
    int Width,
    int Height,
    bool IsMaximized = false,
    bool HasPosition = true);
