using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// What the app shows for each protocol, pinned, so describing protocols in one place can't change it: badge colors
/// and icons, quick connect's guesses, and every section and row the properties pane shows.
/// </summary>
/// <remarks>Set ORRERY_UPDATE_GOLDEN=1 to rewrite the files in Golden/ after a deliberate change.</remarks>
public class ProtocolGuardUiTests
{
    [Theory]
    [InlineData(Protocol.Rdp, "#ff1d2e45", "#ff8cc2ff", nameof(Icons.Monitor))]
    [InlineData(Protocol.Ssh, "#ff183526", "#ff7ee0a6", nameof(Icons.Terminal))]
    [InlineData(Protocol.Vnc, "#ff3a2a17", "#fff3b672", nameof(Icons.Monitor))]
    [InlineData(Protocol.Telnet, "#ff2e2445", "#ffc9b0ff", nameof(Icons.Terminal))]
    [InlineData(Protocol.Serial, "#ff3a2230", "#fff2a7c8", nameof(Icons.SerialPort))]
    [InlineData(Protocol.Http, "#ff173538", "#ff7fd8de", nameof(Icons.Globe))]
    [InlineData(Protocol.Https, "#ff173538", "#ff7fd8de", nameof(Icons.Globe))]
    [InlineData(Protocol.Local, "#ff33331a", "#ffe6e08a", nameof(Icons.Terminal))]
    public void Each_protocols_badge_is_as_it_was(Protocol protocol, string back, string fore, string icon)
    {
        Assert.Equal(back, ((ISolidColorBrush)ProtocolStyle.Background(protocol)).Color.ToString());
        Assert.Equal(fore, ((ISolidColorBrush)ProtocolStyle.Foreground(protocol)).Color.ToString());
        Assert.Equal(typeof(Icons).GetField(icon)?.GetValue(null) ?? typeof(Icons).GetProperty(icon)?.GetValue(null), ProtocolStyle.Icon(protocol));
    }

    [Theory]
    [InlineData("rdp://box", "Rdp:3389")]
    [InlineData("ssh://alice@box:2222", "Ssh:2222:alice")]
    [InlineData("vnc://box", "Vnc:5900")]
    [InlineData("telnet://box", "Telnet:23")]
    [InlineData("serial://COM3", "Serial:0")]
    [InlineData("http://router.lan", "Http:80")]
    [InlineData("https://router.lan:8443", "Https:8443")]
    [InlineData("RDP://box", "Rdp:3389")]
    [InlineData("box", "Rdp:3389,Ssh:22")]
    [InlineData("box:2222", "Rdp:2222,Ssh:2222")]
    [InlineData("alice@box", "Ssh:22:alice,Rdp:3389:alice")]
    public void Quick_connect_guesses_as_it_did(string typed, string expected)
    {
        var candidates = QuickConnectChoice.Candidates(typed)
            .Select(c => c.Username is null ? $"{c.Protocol}:{c.Port}" : $"{c.Protocol}:{c.Port}:{c.Username}");
        Assert.Equal(expected, string.Join(",", candidates));
    }

    [AvaloniaFact]
    public async Task The_protocol_picker_lists_local_first_then_the_rest_in_order()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "a", Host = "a" };
        tree.Add(entry, tree.Root);
        var (panel, window) = Show(tree);
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is Protocol));
        var listed = picker.Items.OfType<ComboBoxItem>().Select(i => $"{i.Content}={i.Tag}");
        Assert.Equal("LOCAL=Local,RDP=Rdp,SSH=Ssh,VNC=Vnc,TELNET=Telnet,SERIAL=Serial,HTTP=Http,HTTPS=Https", string.Join(",", listed));
        window.Close();
    }

    public static TheoryData<string> PaneCases =>
        new(Enum.GetNames<Protocol>().Concat(["Folder", "Defaults", "SshHost", "SerialHost"]));

    /// <summary>Every visible label, heading and placeholder in the properties pane, top to bottom, for one kind of node.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(PaneCases))]
    public async Task The_properties_pane_shows_what_it_did(string kind)
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab" };
        tree.Add(folder, tree.Root);
        ConnectionNode node;
        if (kind == "Folder")
        {
            node = folder;
        }
        else if (kind == "Defaults")
        {
            node = tree.Root;
        }
        else
        {
            var protocol = kind.EndsWith("Host", StringComparison.Ordinal) ? Enum.Parse<Protocol>(kind[..^4]) : Enum.Parse<Protocol>(kind);
            var entry = new ConnectionEntry { Name = kind, Host = "box", Settings = { Protocol = protocol } };
            tree.Add(entry, folder);
            if (kind.EndsWith("Host", StringComparison.Ordinal))
            {
                tree.AddGuest(new ConnectionEntry { Name = "guest", Host = "guest" }, entry);
            }

            node = entry;
        }

        var (panel, window) = Show(tree);
        await panel.ShowAsync(node);
        Dispatcher.UIThread.RunJobs();

        var lines = new List<string>();
        foreach (var control in window.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsEffectivelyVisible)
            {
                continue;
            }

            switch (control)
            {
                case TextBlock { Text: { Length: > 0 } text } when control.FindAncestorOfType<TextBox>() is null && control.FindAncestorOfType<ComboBox>() is null:
                    lines.Add(text);
                    break;
                case TextBox { PlaceholderText: { Length: > 0 } placeholder }:
                    lines.Add($"[{placeholder}]");
                    break;
                case Button { Content: string label }:
                    lines.Add($"<{label}>");
                    break;
            }
        }

        window.Close();
        Golden($"properties.{kind}.txt", string.Join("\n", lines) + "\n");
    }

    static (PropertiesPanel Panel, Window Window) Show(ConnectionTree tree)
    {
        var panel = new PropertiesPanel(tree, new PropertiesPanelTests.EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 420, Height = 6000 };
        window.Show();
        return (panel, window);
    }

    static void Golden(string name, string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Golden", name);
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("ORRERY_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with ORRERY_UPDATE_GOLDEN=1 to write it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
