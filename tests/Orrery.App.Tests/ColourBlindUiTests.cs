using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Orrery.App.Controls;
using Orrery.App.Security;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Nothing rests on colour alone, and the colour-blind setting changes colours in place.</summary>
public class ColourBlindUiTests
{
    sealed class FakeSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 2222, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067 // Part of the interface; this fake never raises it.
        public event Action<byte[]>? DataReceived;
#pragma warning restore CS0067
        public void Change(SessionState state)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, null));
        }

        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void A_session_tab_says_its_state_in_words_and_shape()
    {
        var session = new FakeSession();
        var tab = new SessionTab(session, new Border(), new SessionOrigin("box", Protocol.Ssh, null, () => Task.CompletedTask));
        var glyph = tab.Header.GetLogicalDescendants().OfType<StateGlyph>().Single();

        foreach (var (state, words) in new[]
                 {
                     (SessionState.Connecting, "Connecting"), (SessionState.Connected, "Connected"),
                     (SessionState.Failed, "Failed"), (SessionState.Disconnected, "Disconnected"),
                 })
        {
            session.Change(state);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(state, glyph.State);
            Assert.Equal(words, AutomationProperties.GetName(glyph));
            Assert.Equal($"user@box:2222 · {words}", ToolTip.GetTip(tab.Header));
            // Named after the session and protocol (the screen reader branch); the state is its item status.
            Assert.Equal(words, tab.Header.Status);
            Assert.Equal("box, SSH", ControlAutomationPeer.CreatePeerForElement(tab.Header).GetName());
            Assert.Same(StateGlyph.BrushFor(state), state switch
            {
                SessionState.Connected => Palette.StateUp,
                SessionState.Connecting => Palette.StateConnecting,
                SessionState.Failed => Palette.StateDown,
                _ => Palette.StateEnded,
            });
        }
    }

    [AvaloniaFact]
    public void Every_state_has_its_own_words()
    {
        var states = Enum.GetValues<SessionState>().Select(s => (SessionState?)s).Append(null).ToList();
        Assert.Equal(states.Count, states.Select(StateGlyph.Describe).Distinct().Count());
    }

    [AvaloniaFact]
    public void Errors_and_warnings_carry_an_icon_and_a_word()
    {
        var error = new Notice(NoticeKind.Error, "Couldn't save");
        var warning = new Notice(NoticeKind.Warning, "No way to recover it");
        var info = new Notice(NoticeKind.Info, "Connecting…");

        Assert.Equal("Error: Couldn't save", AutomationProperties.GetName(error));
        Assert.Equal("Warning: No way to recover it", AutomationProperties.GetName(warning));
        Assert.Equal("Connecting…", AutomationProperties.GetName(info));

        var errorIcon = error.Children.OfType<StrokeIcon>().Single();
        var warningIcon = warning.Children.OfType<StrokeIcon>().Single();
        Assert.True(errorIcon.IsVisible);
        Assert.True(warningIcon.IsVisible);
        Assert.Equal(Notice.ErrorIcon, error.IconPath);
        Assert.Equal(Notice.WarningIcon, warning.IconPath);
        Assert.NotEqual(error.IconPath, warning.IconPath);
        Assert.False(info.Children.OfType<StrokeIcon>().Single().IsVisible);

        Assert.Same(Palette.Danger, error.Message.Foreground);
        Assert.Same(Palette.Warning, warning.Message.Foreground);

        // The kind can change with the message, as status lines do.
        info.Kind = NoticeKind.Error;
        info.Text = "Couldn't connect";
        Assert.Equal("Error: Couldn't connect", AutomationProperties.GetName(info));
        Assert.True(info.Children.OfType<StrokeIcon>().Single().IsVisible);
    }

    [AvaloniaFact]
    public void A_changed_host_key_has_a_warning_icon_and_a_first_one_does_not()
    {
        var changed = new HostKeyPromptWindow(new HostKeyPrompt(CertificatePromptKind.Changed, "box", 22, "ssh-ed25519", "new", "old"));
        var heading = changed.GetLogicalDescendants().OfType<Notice>().First();
        Assert.Equal("Warning: The host key for box has changed", AutomationProperties.GetName(heading));
        Assert.True(heading.Children.OfType<StrokeIcon>().Single().IsVisible);

        var first = new HostKeyPromptWindow(new HostKeyPrompt(CertificatePromptKind.Unknown, "box", 22, "ssh-ed25519", "new", null));
        Assert.Empty(first.GetLogicalDescendants().OfType<Notice>());
    }

    [AvaloniaFact]
    public void Switching_palettes_changes_the_shared_brushes_in_place()
    {
        var danger = Palette.Danger;
        int changes = 0;
        void Count(object? sender, EventArgs e) => changes++;
        Palette.Changed += Count;
        try
        {
            Palette.Use(true);
            Assert.Same(danger, Palette.Danger);
            Assert.Equal(Palette.ColourBlind.Danger, Palette.Danger.Color);
            Assert.Equal(Palette.ColourBlind.StateUp, Palette.StateUp.Color);
            Assert.Equal(Palette.ColourBlind.Warning, ((ISolidColorBrush)Dialogs.WarningBrush).Color);

            Palette.Use(true);
            Assert.Equal(1, changes);
        }
        finally
        {
            Palette.Use(false);
            Palette.Changed -= Count;
        }

        Assert.Equal(Palette.Standard.Danger, Palette.Danger.Color);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public void The_palette_is_published_under_shared_names()
    {
        var resources = new ResourceDictionary();
        Palette.AddTo(resources);

        foreach (var key in new[]
                 {
                     "OrreryStateUp", "OrreryStateConnecting", "OrreryStateDown", "OrreryStateEnded",
                     "OrrerySuccess", "OrreryWarning", "OrreryDanger", "OrreryAccent", "OrreryConnected",
                 })
        {
            Assert.True(resources.ContainsKey(key), key);
        }

        Assert.Same(Palette.StateDown, resources["OrreryStateDown"]);
        Assert.Same(Palette.StateUp, resources["OrreryConnected"]);
    }

    [AvaloniaFact]
    public void The_app_resources_are_the_live_palette_brushes()
    {
        var app = new App();
        app.Initialize();

        Assert.Same(Palette.Danger, app.Resources["OrreryDanger"]);
        Assert.Same(Palette.Accent, app.Resources["OrreryAccent"]);
        Assert.Same(Palette.StateUp, app.Resources["OrreryConnected"]);
        Assert.Same(Palette.Warning, app.Resources["OrreryWarning"]);
    }

    [AvaloniaFact]
    public void A_terminal_switches_colour_scheme()
    {
        var view = new TerminalView(new FakeSession());
        Assert.Equal(TerminalPalette.Standard.Ansi[1], view.AnsiColour(1));

        view.ColourScheme = TerminalColourScheme.ColourBlind;
        Assert.Equal(TerminalPalette.ColourBlind.Ansi[1], view.AnsiColour(1));
        Assert.Equal(TerminalPalette.ColourBlind.Ansi[2], view.AnsiColour(2));

        view.ColourScheme = TerminalColourScheme.Standard;
        Assert.Equal(TerminalPalette.Standard.Ansi[2], view.AnsiColour(2));
    }
}
