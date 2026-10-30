using Avalonia;
using Orrery.App.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class RemoteFrameViewTests
{
    [Theory]
    [InlineData(1.0, false, 1200, 800)]
    [InlineData(1.5, false, 1800, 1200)] // Device pixels: sharp, small text.
    [InlineData(1.5, true, 1200, 800)]   // Screen units: enlarged 1.5× to fill the view.
    [InlineData(2.0, true, 1200, 800)]
    public void Asks_for_device_pixels_unless_zoomed(double displayScale, bool zoom, int width, int height)
    {
        Assert.Equal((width, height), RemoteFrameView.DesktopSize(new Size(1200, 800), displayScale, zoom));
    }
}
