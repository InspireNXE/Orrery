using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Sessions;

/// <summary>
/// The settings pane's "This session" tab, for an open remote desktop. What the view can change while it runs (scaling,
/// view only, color correction) changes the open tab at once and lasts until it closes; each change can be kept for
/// next time, which saves it on the connection. The rest were fixed when the session started, so they're listed as
/// saved, with a button to reconnect with them.
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

    readonly ConnectionTree _tree;
    readonly ConnectionEntry _entry;
    readonly List<LiveItem> _items = [];
    readonly TextBlock _changes = new() { FontSize = 12, Foreground = SoftBrush };
    readonly Control _footer;

    /// <param name="enlargeByDefault">The app's "Enlarge remote desktops" setting, for a connection that doesn't set its own.</param>
    public SessionSettingsView(ConnectionTree tree, ConnectionEntry entry, Protocol protocol, RemoteFrameView view, Func<bool> enlargeByDefault)
    {
        _tree = tree;
        _entry = entry;
        var saved = SettingsResolver.Resolve(entry);

        _items.Add(LiveItem.Choice("Scaling", ScalingLabels, () => view.Scaling, value => view.Scaling = value,
            () => SettingsResolver.Resolve(_entry).Scaling.Value, value => _entry.Settings.Scaling = value));

        // Saved only for VNC, which can open watching; an RDP session can still watch for a while.
        _items.Add(LiveItem.Switch("View only", () => view.ViewOnly, value => view.ViewOnly = value,
            () => protocol == Protocol.Vnc && SettingsResolver.Resolve(_entry).ViewOnly.Value,
            protocol == Protocol.Vnc ? value => _entry.Settings.ViewOnly = value : null));
        _items.Add(LiveItem.Switch("Color correction", () => view.AllowsColorCorrection, value => view.AllowsColorCorrection = value,
            () => SettingsResolver.Resolve(_entry).ColorCorrection.Value, value => _entry.Settings.ColorCorrection = value));

        foreach (var item in _items)
        {
            item.Changed += (_, _) => Refresh();
            item.Kept += (_, _) => Keep(item);
        }

        var reconnect = new Button { Content = "Reconnect", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Foreground = ChangedBrush };
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke(this, EventArgs.Empty);
        var fixedRows = new StackPanel { Spacing = 2 };
        foreach (var (label, value) in FixedSettings(saved, protocol, enlargeByDefault()))
        {
            fixedRows.Children.Add(new DockPanel
            {
                Margin = new Thickness(0, 4),
                Children =
                {
                    Pane.Docked(new TextBlock { Text = value, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 180 }, Dock.Right),
                    new TextBlock { Text = label, FontSize = 12, Foreground = SoftBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
        }

        var heading = new TextBlock { Text = "Need a reconnect", FontSize = 13, FontWeight = FontWeight.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, 2);
        var group = new Border
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
                    Note("Fixed when the session started. Change them in Saved settings, then reconnect; reconnecting also drops this session's changes."),
                    fixedRows,
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

        var body = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };
        body.Children.Add(Note("Changes here reach the open tab at once and last until it closes. Saved settings stay as they are unless you keep a change."));
        foreach (var item in _items)
        {
            body.Children.Add(item.View);
        }

        body.Children.Add(group);
        _footer = Pane.Footer(new StackPanel { Spacing = 10, Children = { _changes, Pane.Pair(keepAll, undoAll) } });
        Content = new DockPanel { Children = { Pane.Docked(_footer, Dock.Bottom), new ScrollViewer { Content = body } } };
        Refresh();
    }

    /// <summary>Raised just before a change is saved on the connection.</summary>
    public event EventHandler? Keeping;

    /// <summary>Raised when a change is kept, so the saved settings show it.</summary>
    public event EventHandler? Kept;

    /// <summary>Raised when the view changes, so the session toolbar can say so (View only).</summary>
    public event EventHandler? Applied;

    public event EventHandler? ReconnectRequested;

    /// <summary>How many settings differ from the saved ones, for the tab's badge.</summary>
    public int Changes => _items.Count(i => i.IsChanged);

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
        if (rdp)
        {
            yield return ("Remote audio", saved.RemoteAudio.Value == RemoteAudio.PlayHere ? "Play here" : "Don't play");
            yield return ("Shared drives and folders", saved.ShareAllDrives.Value ? "Every drive"
                : saved.SharedFolders.Value is { Count: > 0 } folders ? string.Join(", ", folders) : "None");
            yield return ("Console session", OnOff(saved.AdminSession.Value));
        }
    }

    static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };

    /// <summary>
    /// One setting the open session takes up at once: its editor, CHANGED and "Keep for next time" while it differs from
    /// the saved value, and that value under it.
    /// </summary>
    sealed class LiveItem
    {
        readonly Func<object> _current, _saved;
        readonly Action<object> _apply;
        readonly Action<object>? _keep;
        readonly Func<object, string> _describe;
        readonly Action<object> _show;
        readonly TextBlock _changed = new() { Text = "CHANGED", FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.4, Foreground = ChangedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        readonly Button _keepButton = new() { Content = "Keep for next time", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _savedLine = new() { FontSize = 11, Foreground = Dialogs.MutedBrush };
        bool _showing;

        LiveItem(string label, Control editor, bool inline, Func<object> current, Action<object> apply, Func<object> saved, Action<object>? keep,
            Func<object, string> describe, Action<object> show)
        {
            _current = current;
            _apply = apply;
            _saved = saved;
            _keep = keep;
            _describe = describe;
            _show = show;
            AutomationProperties.SetName(editor, label);
            AutomationProperties.SetName(_keepButton, $"Keep {label} for next time");
            _keepButton.Click += (_, _) => Kept?.Invoke(this, EventArgs.Empty);

            var head = new DockPanel();
            if (inline)
            {
                head.Children.Add(Pane.Docked(editor, Dock.Right));
            }

            head.Children.Add(Pane.Docked(_keepButton, Dock.Right));
            head.Children.Add(Pane.Docked(_changed, Dock.Right));
            head.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = new StackPanel { Spacing = 8, Children = { head } };
            if (!inline)
            {
                item.Children.Add(editor);
            }

            item.Children.Add(_savedLine);
            View = new Border { BorderBrush = Pane.LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12), Child = item };
        }

        public event EventHandler? Changed;

        public event EventHandler? Kept;

        public Control View { get; }

        public bool IsChanged => !Equals(_current(), _saved());

        public bool CanKeep => _keep is not null;

        public static LiveItem Choice<T>(string label, (T Value, string Label)[] choices, Func<T> current, Action<T> apply, Func<T> saved, Action<T> keep)
            where T : struct
        {
            var box = new ComboBox { Name = ControlName(label), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (value, text) in choices)
            {
                box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
            }

            LiveItem? item = null;
            item = new LiveItem(label, box, inline: false, () => current(), v => apply((T)v), () => saved(), v => keep((T)v),
                v => choices.First(c => Equals(c.Value, v)).Label,
                v => box.SelectedItem = box.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, v)));
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is ComboBoxItem { Tag: T value })
                {
                    item.Set(value);
                }
            };
            return item;
        }

        public static LiveItem Switch(string label, Func<bool> current, Action<bool> apply, Func<bool> saved, Action<bool>? keep)
        {
            var toggle = new ToggleSwitch { Name = ControlName(label), OnContent = null, OffContent = null, MinWidth = 0, Margin = new Thickness(10, 0, -11, 0), VerticalAlignment = VerticalAlignment.Center };
            LiveItem? item = null;
            item = new LiveItem(label, toggle, inline: true, () => current(), v => apply((bool)v), () => saved(), keep is null ? null : v => keep((bool)v),
                v => (bool)v ? "On" : "Off", v => toggle.IsChecked = (bool)v);
            toggle.IsCheckedChanged += (_, _) => item.Set(toggle.IsChecked == true);
            return item;
        }

        /// <summary>"Color correction" as ColorCorrection.</summary>
        static string ControlName(string label) => string.Concat(label.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

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

        /// <summary>Shows the session's value, and whether and how it differs from the saved one.</summary>
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
            _savedLine.Text = $"Saved: {_describe(_saved())}";
        }
    }
}
