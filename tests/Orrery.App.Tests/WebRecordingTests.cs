using Avalonia.Headless.XUnit;
using Orrery.App.Recording;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class WebRecordingTests
{
    [AvaloniaFact]
    public void A_web_page_can_be_recorded_on_windows_only()
    {
        var page = new WebPageView(new Uri("https://router.lan/"), Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(OperatingSystem.IsWindows(), RecordingService.Recordable(page));
    }

    [Fact]
    public void A_web_session_offers_the_page_as_a_screen_with_nothing_to_show_until_a_frame_arrives()
    {
        IFrameBufferSession session = new WebSession(new ConnectionTarget("router.lan", 443, null, null), new WebFrames());
        Assert.Equal(0, session.Width);
        Assert.Equal(0, session.Height);
        // Copying from an empty picture is a no-op rather than a crash.
        session.CopyRegion(new FrameRect(0, 0, 10, 10), IntPtr.Zero, 40);
    }
}
