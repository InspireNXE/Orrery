using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// The properties pane's "This session" tab, for an open remote desktop. What the view can change while it runs (scaling,
/// view only, color correction) changes the open tab at once and lasts until it closes; each change can be kept for
/// next time, which saves it on the connection. The rest were fixed when the session started; once any of those is
/// saved differently, a card lists the changes with a button to reconnect with them. The Connection card under them
/// shows how the connection is doing; a terminal's tab has only that.
/// </summary>
sealed class SessionSettingsView : UserControl
{
    static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#F0B455")).ToImmutable();
    static readonly IBrush GroupBrush = new SolidColorBrush(Color.Parse("#11151B")).ToImmutable();
    static readonly IBrush SoftBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();

    internal static readonly (DesktopScaling Scaling, string Label)[] ScalingLabels =
    [
        (DesktopScaling.Fit, "Fit the tab"),
        (DesktopScaling.ShrinkToFit, "Shrink to fit"),
        (DesktopScaling.ActualSize, "Actual size"),
    ];

    /// <summary>Where Alt+Tab, Alt+Esc and Ctrl+Esc go, as the choices read here and in a connection's properties.</summary>
    internal static readonly (SystemShortcuts Shortcuts, string Label)[] ShortcutLabels =
    [
        (SystemShortcuts.FullScreen, "To the remote in full screen"),
        (SystemShortcuts.Remote, "Always to the remote"),
        (SystemShortcuts.Local, "Always to this computer"),
    ];

    /// <summary>The scaling choices as the session tab's buttons name them, where there's less room.</summary>
    static string ScalingShort(DesktopScaling scaling) => scaling switch
    {
        DesktopScaling.Fit => "Fit",
        DesktopScaling.ShrinkToFit => "Shrink",
        _ => "Actual size",
    };

    static string ShortcutsShort(SystemShortcuts shortcuts) => shortcuts switch
    {
        SystemShortcuts.FullScreen => "Full screen",
        SystemShortcuts.Remote => "Remote",
        _ => "Local",
    };

    /// <summary>The settings the This session tab can change, by the keys <see cref="SessionOnly"/> uses.</summary>
    internal const string ScalingKey = "Scaling", ColorCorrectionKey = "ColorCorrection", ViewOnlyKey = "ViewOnly", ShortcutsKey = "SystemShortcuts";

    readonly ConnectionTree _tree;
    readonly ConnectionEntry _entry;
    readonly List<LiveItem> _items = [];
    readonly TextBlock _changes = new() { FontSize = 12, Foreground = SoftBrush };
    readonly Control _footer;
    readonly Protocol _protocol;
    readonly Func<bool> _enlargeByDefault;
    readonly IReadOnlyList<(string Label, string Value)> _startedWith;
    readonly StackPanel _fixedRows = new() { Spacing = 2 };
    readonly Border _reconnectGroup;
    readonly Control? _display, _input;

    /// <param name="enlargeByDefault">The app's "Enlarge remote desktops" setting, for a connection that doesn't set its own.</param>
    /// <param name="startedWith">The fixed settings as they were when the session started (<see cref="FixedSettingsNow"/>).</param>
    /// <param name="view">The remote desktop, whose view settings can change; null for a terminal, which has only the Connection card.</param>
    /// <param name="link">How the connection is doing, for the Connection card; no card when null.</param>
    public SessionSettingsView(ConnectionTree tree, ConnectionEntry entry, Protocol protocol, RemoteFrameView? view, Func<bool> enlargeByDefault,
        IReadOnlyList<(string Label, string Value)> startedWith, LinkMonitor? link = null)
    {
        _tree = tree;
        _entry = entry;

        if (view is not null)
        {
            // While the server resizes the desktop to the tab it always fits, so the choice waits for a fixed size.
            var scaling = LiveItem.Choice(ScalingKey, "Scaling", ScalingLabels, ScalingShort, () => view.Scaling, value => view.Scaling = value,
                () => SettingsResolver.Resolve(_entry).Scaling.Value, value => _entry.Settings.Scaling = value,
                () => view.ResizesToView ? "No effect now: the desktop resizes to fit the tab. It applies with a fixed desktop size." : null);
            view.ResizesToViewChanged += (_, _) => scaling.Refresh();
            var correction = LiveItem.Switch(ColorCorrectionKey, "Color correction", () => view.AllowsColorCorrection, value => view.AllowsColorCorrection = value,
                () => SettingsResolver.Resolve(_entry).ColorCorrection.Value, value => _entry.Settings.ColorCorrection = value);
            _items.Add(scaling);
            _items.Add(correction);
            _display = Pane.Section("Display", scaling.View, correction.View);

            // Saved only for VNC, which can open watching; an RDP session can still watch for a while.
            var viewOnly = LiveItem.Switch(ViewOnlyKey, "View only", () => view.ViewOnly, value => view.ViewOnly = value,
                () => protocol == Protocol.Vnc && SettingsResolver.Resolve(_entry).ViewOnly.Value,
                protocol == Protocol.Vnc ? value => _entry.Settings.ViewOnly = value : null);
            var input = new List<Control> { viewOnly.View };

            // Only Windows lets Orrery take Alt+Tab from the system. Saved is the connection's choice, else Settings'.
            if (OperatingSystem.IsWindows())
            {
                var shortcuts = LiveItem.Choice(ShortcutsKey, "Alt+Tab goes to", ShortcutLabels, ShortcutsShort,
                    () => view.SessionShortcuts, value => view.SessionShortcuts = value, () => view.SavedShortcuts, value => _entry.Settings.SystemShortcuts = value);
                _items.Add(shortcuts);
                input.Insert(0, shortcuts.View);
            }

            _items.Add(viewOnly);
            _input = Pane.Section("Keyboard and mouse", [.. input]);
        }

        foreach (var item in _items)
        {
            item.Changed += (_, _) => Refresh();
            item.Kept += (_, _) => Keep(item);
        }

        _protocol = protocol;
        _enlargeByDefault = enlargeByDefault;
        _startedWith = startedWith;
        var reconnect = new Button { Content = "Reconnect", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Foreground = ChangedBrush };
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);

        var heading = new TextBlock { Text = "On next connect", FontSize = 13, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, 2);
        _reconnectGroup = new Border
        {
            Background = GroupBrush,
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    heading,
                    Note("Saved differently since this session started. They take effect when it reconnects, which also drops this session's own changes."),
                    _fixedRows,
                    reconnect,
                },
            },
        };

        var keepAll = new Button { Content = "Keep for next time", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        keepAll.Click += (_, _) =>
        {
            foreach (var item in _items.Where(i => i.IsChanged && i.CanKeep).ToList())
            {
                Keep(item);
            }
        };
        var undoAll = new Button { Content = "Undo", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        undoAll.Click += (_, _) =>
        {
            foreach (var item in _items)
            {
                item.Undo();
            }
        };

        var body = new StackPanel { Margin = new Thickness(16, 12, 16, 16) };
        if (view is not null)
        {
            body.Children.Add(Note("Changes here reach the open tab at once and last until it closes. Saved settings stay as they are unless you keep a change."));
        }

        if (_display is not null)
        {
            body.Children.Add(_display);
            body.Children.Add(_input!);
        }

        body.Children.Add(_reconnectGroup);
        if (link is not null)
        {
            body.Children.Add(new ConnectionCard(link, ProtocolName(protocol)));
        }
        _footer = Pane.Footer(new StackPanel { Spacing = 10, Children = { _changes, Pane.Pair(keepAll, undoAll) } });
        Content = new DockPanel { Children = { Pane.Docked(_footer, Dock.Bottom), new ScrollViewer { Content = body } } };
        Refresh();
        ShowReconnectChanges();

        // Saved settings can change while this is open; follow them only while it's shown, so a dropped view isn't kept alive.
        AttachedToVisualTree += (_, _) =>
        {
            _tree.Changed += OnTreeChanged;
            ShowReconnectChanges();
        };
        DetachedFromVisualTree += (_, _) => _tree.Changed -= OnTreeChanged;
    }

    void OnTreeChanged(object? sender, EventArgs e) => ShowReconnectChanges();

    /// <summary>
    /// Lists the settings a running session can't take up whose saved value is no longer the one it started with, and
    /// hides the reconnect card when there are none.
    /// </summary>
    void ShowReconnectChanges()
    {
        var now = FixedSettings(SettingsResolver.Resolve(_entry), _protocol, _enlargeByDefault()).ToList();
        _fixedRows.Children.Clear();
        foreach (var (label, value) in now)
        {
            var then = _startedWith.FirstOrDefault(s => s.Label == label).Value;
            if (then is null || then == value)
            {
                continue;
            }

            // What this session has, then what the next connect brings.
            var change = new TextBlock { Text = $"{then} → {value}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200, Margin = new Thickness(10, 0, 0, 0) };
            AutomationProperties.SetName(change, $"{label}: {then} now, {value} after reconnecting");
            _fixedRows.Children.Add(new DockPanel
            {
                Margin = new Thickness(0, 4),
                Children =
                {
                    Pane.Docked(change, Dock.Right),
                    new TextBlock { Text = label, FontSize = 12, Foreground = SoftBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
        }

        _reconnectGroup.IsVisible = _fixedRows.Children.Count > 0;
    }

    /// <summary>What the settings fixed at the start were for <paramref name="entry"/> right now, to compare with later.</summary>
    internal static IReadOnlyList<(string Label, string Value)> FixedSettingsNow(ConnectionEntry entry, Protocol protocol, bool enlargeByDefault) =>
        FixedSettings(SettingsResolver.Resolve(entry), protocol, enlargeByDefault).ToList();

    /// <summary>Raised just before a change is saved on the connection.</summary>
    public event EventHandler? Keeping;

    /// <summary>Raised when a change is kept, so the saved settings show it.</summary>
    public event EventHandler? Kept;

    /// <summary>Raised when the view changes, so the session toolbar can say so (View only).</summary>
    public event EventHandler? Applied;

    public event EventHandler? ReconnectRequested;

    /// <summary>How many settings differ from the saved ones, for the tab's badge.</summary>
    public int Changes => _items.Count(i => i.IsChanged);

    /// <summary>The settings this session has changed and not kept, by key (<see cref="ScalingKey"/> and the rest), as they read.</summary>
    public IReadOnlyDictionary<string, string> SessionOnly => _items.Where(i => i.IsChanged).ToDictionary(i => i.Key, i => i.Describe());

    void Keep(LiveItem item)
    {
        Keeping?.Invoke(this, EventArgs.Empty);
        item.Keep();
        _tree.NotifyChanged();
        Refresh();
        Kept?.Invoke(this, EventArgs.Empty);
    }

    void Refresh()
    {
        foreach (var item in _items)
        {
            item.Refresh();
        }

        int changes = Changes;
        _footer.IsVisible = changes > 0;
        _changes.Text = changes == 1 ? "1 change for this session only" : $"{changes} changes for this session only";
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The saved settings a running session can't take up, as they'd be used on reconnecting.</summary>
    static IEnumerable<(string Label, string Value)> FixedSettings(ResolvedSettings saved, Protocol protocol, bool enlargeByDefault)
    {
        static string OnOff(bool on) => on ? "On" : "Off";
        bool rdp = protocol == Protocol.Rdp;
        yield return ("Display quality", PropertiesPanel.QualityLabels.First(q => q.Quality == saved.DisplayQuality.Value).Label);
        if (rdp)
        {
            yield return ("Remote desktop size", string.IsNullOrEmpty(saved.DesktopSize.Value) ? "Fit the window" : saved.DesktopSize.Value!);
        }

        yield return ("Match display scaling", OnOff(saved.Enlarge(enlargeByDefault)));
        yield return ("Share the clipboard", OnOff(saved.ShareClipboard.Value));
        yield return ("Reconnect if it drops", OnOff(saved.AutoReconnect.Value));
        yield return ("Record sessions", OnOff(saved.RecordSessions.Value));
        if (!rdp && saved.RecordTyping.Value)
        {
            yield return ("Record what's typed too", "On");
        }

        if (rdp)
        {
            yield return ("Remote audio", saved.RemoteAudio.Value == RemoteAudio.PlayHere ? "Play here" : "Don't play");
            yield return ("Shared drives and folders", saved.ShareAllDrives.Value ? "Every drive"
                : saved.SharedFolders.Value is { Count: > 0 } folders ? string.Join(", ", folders) : "None");
            yield return ("Console session", OnOff(saved.AdminSession.Value));
        }
    }

    static string ProtocolName(Protocol protocol) => protocol switch
    {
        Protocol.Rdp => "RDP",
        Protocol.Vnc => "VNC",
        Protocol.Ssh => "SSH",
        Protocol.Telnet => "Telnet",
        _ => protocol.ToString(),
    };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    /// <summary>
    /// One setting the open session takes up at once: its editor, CHANGED while it differs from the saved value, and
    /// under it the saved value with Keep for next time and Undo. A choice is a row of buttons; on or off is a switch.
    /// A note under it says when the setting can't do anything at the moment.
    /// </summary>
    sealed class LiveItem
    {
        readonly Func<object> _current, _saved;
        readonly Action<object> _apply;
        readonly Action<object>? _keep;
        readonly Func<object, string> _describe;
        readonly Action<object> _show;
        readonly Func<string?>? _note;
        readonly TextBlock _changed = new() { Text = "CHANGED", FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.4, Foreground = ChangedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        readonly Button _keepButton = new() { Content = "Keep for next time", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        readonly Button _undoButton = new() { Content = "Undo", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        readonly TextBlock _savedText = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly DockPanel _savedLine;
        readonly TextBlock _noteText = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
        bool _showing;

        LiveItem(string key, string label, Control editor, bool inline, Func<object> current, Action<object> apply, Func<object> saved, Action<object>? keep,
            Func<object, string> describe, Action<object> show, Func<string?>? note)
        {
            Key = key;
            _current = current;
            _apply = apply;
            _saved = saved;
            _keep = keep;
            _describe = describe;
            _show = show;
            _note = note;
            AutomationProperties.SetName(editor, label);
            AutomationProperties.SetName(_keepButton, $"Keep {label} for next time");
            AutomationProperties.SetName(_undoButton, $"Undo {label}");
            _keepButton.Click += (_, _) => Kept?.Invoke(this, EventArgs.Empty);
            _undoButton.Click += (_, _) => Undo();

            var head = new DockPanel();
            if (inline)
            {
                head.Children.Add(Pane.Docked(editor, Dock.Right));
            }

            head.Children.Add(Pane.Docked(_changed, Dock.Right));
            head.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new StackPanel { Spacing = 8, Children = { head } };
            if (!inline)
            {
                item.Children.Add(editor);
            }

            _savedLine = new DockPanel { Children = { Pane.Docked(_undoButton, Dock.Right), Pane.Docked(_keepButton, Dock.Right), _savedText } };
            item.Children.Add(_savedLine);
            item.Children.Add(_noteText);
            View = new Border { Padding = new Thickness(0, 4), Child = item };
        }

        public event EventHandler? Changed;

        public event EventHandler? Kept;

        /// <summary>Which setting this is, as <see cref="SessionOnly"/> names it.</summary>
        public string Key { get; }

        public Control View { get; }

        public bool IsChanged => !Equals(_current(), _saved());

        public bool CanKeep => _keep is not null;

        /// <summary>The session's value as it reads.</summary>
        public string Describe() => _describe(_current());

        /// <summary>A choice as a row of buttons, one selected, each named by <paramref name="shortLabel"/> where room is short.</summary>
        public static LiveItem Choice<T>(string key, string label, (T Value, string Label)[] choices, Func<T, string> shortLabel, Func<T> current, Action<T> apply,
            Func<T> saved, Action<T> keep, Func<string?>? note = null)
            where T : struct
        {
            var grid = new Grid { ColumnSpacing = 4 };
            var buttons = new List<Button>();
            LiveItem? item = null;
            foreach (var (value, text) in choices)
            {
                var button = new Button { Content = shortLabel(value), Tag = value, Classes = { "segment" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(button, text);
                AutomationProperties.SetName(button, $"{label}: {text}");
                button.Click += (_, _) => item!.Set(value);
                Grid.SetColumn(button, buttons.Count);
                grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                grid.Children.Add(button);
                buttons.Add(button);
            }

            var row = new Border { Name = ControlName(label), Classes = { "segments" }, Child = grid };
            item = new LiveItem(key, label, row, inline: false, () => current(), v => apply((T)v), () => saved(), v => keep((T)v),
                v => choices.First(c => Equals(c.Value, v)).Label,
                v =>
                {
                    foreach (var button in buttons)
                    {
                        bool selected = Equals(button.Tag, v);
                        button.Classes.Set("selected", selected);
                        AutomationProperties.SetItemStatus(button, selected ? "Selected" : string.Empty);
                    }
                },
                note);
            return item;
        }

        public static LiveItem Switch(string key, string label, Func<bool> current, Action<bool> apply, Func<bool> saved, Action<bool>? keep)
        {
            var toggle = new ToggleSwitch { Name = ControlName(label), OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(10, 0, -11, 0), VerticalAlignment = VerticalAlignment.Center };
            LiveItem? item = null;
            item = new LiveItem(key, label, toggle, inline: true, () => current(), v => apply((bool)v), () => saved(), keep is null ? null : v => keep((bool)v),
                v => (bool)v ? "On" : "Off", v => toggle.IsChecked = (bool)v, null);
            toggle.IsCheckedChanged += (_, _) => item.Set(toggle.IsChecked == true);
            return item;
        }

        /// <summary>"Color correction" as ColorCorrection, "Alt+Tab goes to" as AltTabGoesTo.</summary>
        static string ControlName(string label) =>
            string.Concat(label.Replace("+", " ", StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

        /// <summary>The user picked <paramref name="value"/>: the open session takes it up.</summary>
        void Set(object value)
        {
            if (_showing || Equals(value, _current()))
            {
                return;
            }

            _apply(value);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Keep() => _keep?.Invoke(_current());

        /// <summary>Puts the saved value back on the open session.</summary>
        public void Undo()
        {
            if (IsChanged)
            {
                _apply(_saved());
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Shows the session's value, whether and how it differs from the saved one, and any note.</summary>
        public void Refresh()
        {
            _showing = true;
            try
            {
                _show(_current());
            }
            finally
            {
                _showing = false;
            }

            bool changed = IsChanged;
            _changed.IsVisible = changed;
            _keepButton.IsVisible = changed && _keep is not null;
            _savedLine.IsVisible = changed;
            _savedText.Text = $"Saved: {_describe(_saved())}";
            string? note = _note?.Invoke();
            _noteText.Text = note;
            _noteText.IsVisible = note is not null;
        }
    }
}
