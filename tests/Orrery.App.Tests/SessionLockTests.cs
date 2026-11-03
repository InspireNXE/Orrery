using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class SessionLockTests
{
    static SessionTab Tab(Protocol protocol, Control? view = null, bool local = false) =>
        new(new WebSession(new ConnectionTarget("box", 22, null, null)), view ?? new Border(),
            new SessionOrigin("box", protocol, null, () => Task.CompletedTask) { Local = local });

    [AvaloniaFact]
    public void Only_ssh_and_rdp_count_as_encrypted_and_local_ones_have_no_lock()
    {
        Assert.True(MainWindow.Encrypted(Tab(Protocol.Ssh)));
        Assert.True(MainWindow.Encrypted(Tab(Protocol.Rdp)));
        Assert.False(MainWindow.Encrypted(Tab(Protocol.Telnet)));
        Assert.False(MainWindow.Encrypted(Tab(Protocol.Vnc)));
        Assert.Null(MainWindow.Encrypted(Tab(Protocol.Serial)));
        Assert.Null(MainWindow.Encrypted(Tab(Protocol.Ssh, local: true)));
    }

    [AvaloniaFact]
    public void A_web_page_is_encrypted_by_its_own_address_not_the_connection_it_came_from()
    {
        string data = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
        Assert.False(MainWindow.Encrypted(Tab(Protocol.Https, new WebPageView(new Uri("http://router.lan/"), data))));
        Assert.True(MainWindow.Encrypted(Tab(Protocol.Http, new WebPageView(new Uri("https://router.lan/"), data))));
    }
}
