using CsWebUi;

namespace CsWebUi.Tests;

public sealed class WebUiPlacementTests
{
    [Fact]
    public void PlacementDefaultsToARestoredWindowWithAPosition()
    {
        var placement = new WebUiWindowPlacement(-1280, 40, 900, 700);

        Assert.Equal(-1280, placement.X);
        Assert.False(placement.IsMaximized);
        Assert.True(placement.HasPosition);
    }

    [NativeFact]
    public void AWindowThatIsNotShownHasNoPlacement()
    {
        using var window = new WebUiWindow();

        Assert.Equal(0, window.NativeHandle);
        Assert.False(window.TryGetPlacement(out var placement));
        Assert.Equal(default, placement);
        Assert.False(window.TrySetPlacement(new WebUiWindowPlacement(10, 10, 640, 480)));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.TrySetPlacement(new WebUiWindowPlacement(0, 0, 0, 480)));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.TrySetPlacement(new WebUiWindowPlacement(0, 0, 640, -1)));
    }

    [NativeFact]
    public void ADisposedWindowRefusesPlacementCalls()
    {
        var window = new WebUiWindow();
        window.Dispose();

        Assert.Throws<ObjectDisposedException>(() => window.NativeHandle);
        Assert.Throws<ObjectDisposedException>(() => window.TryGetPlacement(out _));
        Assert.Throws<ObjectDisposedException>(() => window.TrySetPlacement(new WebUiWindowPlacement(0, 0, 640, 480)));
    }
}
