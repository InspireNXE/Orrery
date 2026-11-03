using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Recording;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;

namespace Orrery.App.Recording;

/// <summary>
/// Plays a recording in the Recordings section's main area (docs/design-session-recording.md §5.3): a terminal through a read-only terminal view, a screen
/// through the decoder into a bitmap. A header says what was recorded and when, with Find for a terminal and Export;
/// under the picture are the timeline with its markers, play and pause, 10 seconds back and forward, speed and Skip
/// idle. Seeking back replays a terminal's output from the start, and a screen's from the key frame before.
/// </summary>
sealed partial class PlayerView : DockPanel, IDisposable
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);
    static readonly TimeSpan SkipBy = TimeSpan.FromSeconds(10);
    static readonly double[] Speeds = [0.5, 1, 2, 4, 8];
    static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#101216")).ToImmutable();
    static readonly IBrush ControlBorder = new SolidColorBrush(Color.Parse("#2A2F38")).ToImmutable();
    static readonly IBrush NoteBrush = new SolidColorBrush(Color.Parse("#EB101216")).ToImmutable();
    static readonly IBrush ShadeBrush = new SolidColorBrush(Color.Parse("#99080A0E")).ToImmutable();
    static readonly IBrush SoftText = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();

    // Half the slider's thumb: the track's ends sit this far in from the control's edges.
    const double ThumbInset = 10;
    const int MaxMatches = 10_000;

    /// <summary>Find matches drawn on the timeline; past this many they'd only be a smear.</summary>
    const int MostMatchTicks = 500;

    /// <summary>One event on the timeline. Terminal events keep their payload; a screen's big frames are read again when needed.</summary>
    sealed record Entry(RecordingEventType Type, TimeSpan Recorded, EventPosition Position, byte[]? Payload)
    {
        public TimeSpan Played { get; set; }
    }

    readonly RecordingService _service;
    readonly RecordingItem _item;
    readonly byte[] _privateKey;
    readonly IDisposable _playing;
    readonly List<Entry> _entries = [];
    readonly DispatcherTimer _timer = new() { Interval = Tick };
    readonly Panel _stage = new() { Background = Brushes.Black, ClipToBounds = true };

    // Header: one line, like a session's toolbar. The title and subtitle sit in a dock panel so a long name ends in
    // "…" before the buttons instead of running under them.
    readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _protocolText = new() { FontSize = 10, FontWeight = FontWeight.Medium, FontFamily = Dialogs.MonoFont, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _protocol = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 12, 0) };
    readonly Notice _banner = new(NoticeKind.Warning) { IsVisible = false, Margin = new Thickness(16, 10, 16, 0) };

    // Find, for a terminal: one box holding the search, the count and the arrows.
    readonly TextBox _find = new() { PlaceholderText = "Find in output", Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _matches = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
    readonly Button _findPrevious = IconButton(Icons.Previous, "Previous match", "Shift+Enter");
    readonly Button _findNext = IconButton(Icons.Next, "Next match", "Enter");
    readonly Panel _findBar = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsVisible = false };

    // Transport.
    readonly Button _playButton = new() { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StrokeIcon _playIcon = StrokeIcon.Create(Icons.Play, 18);
    readonly Slider _timeline = new() { Minimum = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    readonly Canvas _ticks = new() { IsHitTestVisible = true };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush };
    readonly ComboBox _speed = new() { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
    readonly Button _skipIdle = Toggle(Icons.SkipIdle, "Skip idle");
    readonly Button _loop = Toggle(Icons.Loop, "Loop");
    bool _skipIdleOn = true;

    /// <summary>Whether a recording starts again when it ends. Off at first; kept for the next one played while Orrery runs.</summary>
    static bool s_loop;

    // Over the picture: a note at the end, and the keyboard shortcuts.
    readonly Border _endNote = new()
    {
        Background = NoteBrush,
        BorderBrush = ControlBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 6),
        Margin = new Thickness(16, 0, 16, 16),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        IsVisible = false,
        Child = new TextBlock { Text = "End of recording. Play to watch it again, or turn on Loop.", FontSize = 12, TextWrapping = TextWrapping.Wrap },
    };
    readonly Panel _keys = new() { Background = ShadeBrush, IsVisible = false };
    readonly Grid _keyRows = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 4, 16, 16) };

    // Sound: a speaker that mutes, and how loud. Kept for the next recording played while Orrery runs.
    static double s_volume = 0.8;
    static bool s_muted;
    readonly StrokeIcon _speakerIcon = StrokeIcon.Create(Icons.Volume, 16);
    readonly Button _speaker = new() { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
    readonly Slider _volume = new() { Minimum = 0, Maximum = 100, Width = 84, VerticalAlignment = VerticalAlignment.Center, Classes = { "volume" } };
    readonly StackPanel _sound = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), IsVisible = false };

    // Colors as the viewer has them set: recordings keep the original colors, so they're corrected as they play.
    ColorVision _vision;
    TerminalColorScheme _scheme;
    ColorCorrection? _correction;

    // What a screen is drawn with: the viewer's correction, unless the connection turned it off, after undoing any
    // the pictures were recorded with (a web page's).
    ColorCorrection? _drawWith;

    RecordingKind _kind;
    RecordingEnding _ending;
    RecordingInfo? _info;
    TimeSpan _duration;
    TimeSpan _position;
    DateTime _lastTick;

    /// <summary>Opens this computer's speakers for recorded sound; tests swap it.</summary>
    internal static Func<int, int, IAudioOutput?> OpenAudio = Orrery.Protocols.Rdp.RdpAudioOutput.TryOpen;

    /// <summary>Sound further behind the playing position than this is left out: it was passed over, not played.</summary>
    static readonly TimeSpan SoundLate = TimeSpan.FromMilliseconds(300);

    /// <summary>Sound plays up to this speed, faster or slower at the same pitch; beyond it, it's too quick to follow.</summary>
    const double MostAudibleSpeed = 4;

    IAudioOutput? _audio;
    (int Rate, int Channels) _audioFormat;
    SoundStretch? _stretch;
    double _playSpeed = 1;
    bool _audible;
    TimeSpan _advancingTo;
    bool _loaded;
    bool _playingNow;
    bool _settingTimeline;
    int _next;

    // Terminal playback.
    PlaybackSession? _terminalSession;
    TerminalView? _terminal;
    Viewbox? _terminalBox;
    string _text = string.Empty;
    int[] _textEvent = [];
    List<int> _found = [];
    int _foundAt = -1;

    // Screen playback.
    RecordingReader? _reader;
    IEnumerator<RecordedEvent>? _cursor;
    readonly ScreenDecoder _decoder = new();
    readonly Image _image = new() { Stretch = Stretch.Uniform };
    WriteableBitmap? _bitmap;

    bool _started;
    bool _disposed;

    /// <summary>The ⋯ menu's Show in folder.</summary>
    public event EventHandler? ShowInFolderRequested;

    /// <summary>The ⋯ menu's Delete recording, or the Delete key.</summary>
    public event EventHandler? DeleteRequested;

    public PlayerView(RecordingService service, RecordingItem item, byte[] privateKey)
    {
        _service = service;
        _item = item;
        _privateKey = privateKey;
        _playing = service.Playing(item.Path);
        _info = item.Info;
        var settings = service.Settings;
        _vision = settings.Vision;
        _scheme = settings.TerminalColors ?? TerminalColorScheme.Standard;
        _correction = ColorCorrection.For(settings.Vision, settings.CorrectionStrength);
        ChooseCorrection();
        Background = Dialogs.PanelBrush;
        Focusable = true;
        ShowWhat();

        foreach (var speed in Speeds)
        {
            _speed.Items.Add(new ComboBoxItem { Content = $"{speed:0.#}×", Tag = speed });
        }

        _speed.SelectedIndex = 1;
        ToolTip.SetTip(_speed, Tip("Playback speed", "[  ]"));
        AutomationProperties.SetName(_speed, "Playback speed");
        AutomationProperties.SetName(_timeline, "Position");
        AutomationProperties.SetName(_find, "Find in output");

        _playButton.Classes.Add("accent");
        _playButton.Content = _playIcon;
        _playButton.Click += (_, _) => TogglePlay();
        SetPlayLook(playing: false);
        var back = SkipButton(Icons.SkipBack, "Back 10 seconds", "←", () => Seek(_position - SkipBy));
        var forward = SkipButton(Icons.SkipForward, "Forward 10 seconds", "→", () => Seek(_position + SkipBy));

        _timeline.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_settingTimeline)
            {
                Seek(TimeSpan.FromSeconds(_timeline.Value));
            }
        };

        ToolTip.SetTip(_skipIdle, "Cut pauses longer than 2 seconds down to 2");
        SetOn(_skipIdle, _skipIdleOn);
        _skipIdle.Click += (_, _) => SetSkipIdle(!_skipIdleOn);
        ToolTip.SetTip(_loop, Tip("Play again from the start when it ends", "L"));
        SetOn(_loop, s_loop);
        _loop.Click += (_, _) => ToggleLoop();

        _find.TextChanged += (_, _) => FindAll();
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FindStep((e.KeyModifiers & KeyModifiers.Shift) != 0 ? -1 : 1);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !string.IsNullOrEmpty(_find.Text))
            {
                _find.Text = string.Empty;
                e.Handled = true;
            }
        };
        _findPrevious.Click += (_, _) => FindStep(-1);
        _findNext.Click += (_, _) => FindStep(1);
        _findPrevious.Width = _findPrevious.Height = _findNext.Width = _findNext.Height = 26;
        _find.InnerLeftContent = new StrokeIcon { Data = Geometry.Parse(Icons.Search), Width = 14, Height = 14, Foreground = Dialogs.MutedBrush, IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0) };
        _find.InnerRightContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            Children = { _matches, new Border { Width = 1, Height = 18, Background = ControlBorder, Margin = new Thickness(0, 0, 2, 0) }, _findPrevious, _findNext },
        };
        _findBar.Children.Add(_find);

        var export = IconButton(Icons.Download, "Export an unencrypted copy…", null);
        AutomationProperties.SetName(export, "Export");
        export.Click += async (_, _) =>
        {
            Pause();
            if (TopLevel.GetTopLevel(this) is not Window owner)
            {
                return;
            }

            await RecordingExport.ExportAsync(owner, _service, _item.Path, _privateKey, System.IO.Path.GetFileNameWithoutExtension(_item.Path));
        };

        Button more = null!;
        more = IconButton(Icons.More, "More options", null);
        more.Click += (_, _) => PopupMenu.ShowAt(more, MoreMenu(), PlacementMode.BottomEdgeAlignedRight);

        _protocol.Child = _protocolText;
        var header = new Border
        {
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 6, 10, 6),
            MinHeight = 48,
            Child = new DockPanel
            {
                Children =
                {
                    Docked(more, Dock.Right),
                    Docked(export, Dock.Right),
                    Docked(_findBar, Dock.Right),
                    Docked(_protocol, Dock.Left),

                    // Clipped, so in a narrow window the subtitle's "…" and margins stop at the find box.
                    new DockPanel { ClipToBounds = true, Children = { Docked(_title, Dock.Left), _subtitle } },
                },
            },
        };

        var timeline = new Grid { Children = { _timeline, _ticks } };
        timeline.SizeChanged += (_, _) => PlaceTicks();

        // The time on the left, the buttons in the middle and how it plays on the right, as in most players.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { back, _playButton, forward } };
        _speaker.Classes.Add("icon");
        _speaker.Content = _speakerIcon;
        _speaker.Click += (_, _) => ToggleMute();
        _volume.Value = s_volume * 100;
        AutomationProperties.SetName(_volume, "Volume");
        _volume.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
            {
                s_volume = _volume.Value / 100;
                s_muted = s_volume == 0;
                ShowVolume();
            }
        };
        _sound.Children.Add(_speaker);
        _sound.Children.Add(_volume);
        ShowVolume();
        var keys = IconButton(Icons.Keyboard, "Keyboard shortcuts", "?");
        keys.Width = keys.Height = 28;
        keys.Click += (_, _) => ShowKeys(!_keys.IsVisible);
        var how = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _sound, _skipIdle, _loop, _speed, keys },
        };
        Grid.SetColumn(buttons, 1);
        Grid.SetColumn(how, 2);
        var transport = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Children = { _time, buttons, how },
        };

        // Docked along the bottom with a line above, like the app's other bars.
        var bar = new Border
        {
            Background = BarBrush,
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 4, 16, 10),
            Child = new StackPanel { Children = { timeline, transport } },
        };

        BuildKeys();
        _stage.Children.Add(new TextBlock { Text = "Reading the recording…", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(Docked(header, Dock.Top));
        Children.Add(Docked(_banner, Dock.Top));
        Children.Add(Docked(bar, Dock.Bottom));

        // The picture runs edge to edge, as a live session's does.
        Children.Add(new Panel { Children = { _stage, _endNote, _keys } });

        _timer.Tick += (_, _) => OnTick();
        AttachedToVisualTree += async (_, _) =>
        {
            if (!_started)
            {
                _started = true;
                await LoadAsync();
            }
        };

        // Tunnel, so Space plays and pauses wherever focus is rather than pressing the focused button.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    /// <summary>The recording this plays.</summary>
    public string Path => _item.Path;

    /// <summary>What the header says is playing.</summary>
    public string Heading => _title.Text ?? string.Empty;

    /// <summary>Stops playing and lets go of the recording, so it can be deleted, and of its key.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _audio?.Dispose();
        _audio = null;
        _cursor?.Dispose();
        _reader?.Dispose();
        _playing.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_privateKey);
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    /// <summary>A tooltip that names its shortcut in a key cap after the words.</summary>
    static object Tip(string text, string? key)
    {
        if (key is null)
        {
            return text;
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                KeyCap(key),
            },
        };
    }

    static Border KeyCap(string key) => new()
    {
        BorderBrush = ControlBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Child = new TextBlock { Text = key, FontFamily = Dialogs.MonoFont, FontSize = 11, Foreground = Dialogs.MutedBrush },
    };

    static Button IconButton(string icon, string name, string? key)
    {
        var button = new Button { Content = StrokeIcon.Create(icon, 16), Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, Tip(name, key));
        AutomationProperties.SetName(button, name.TrimEnd('…'));
        return button;
    }

    /// <summary>A round arrow with "10" inside it.</summary>
    static Button SkipButton(string icon, string name, string key, Action click)
    {
        var button = IconButton(icon, name, key);
        button.Width = button.Height = 34;
        button.CornerRadius = new CornerRadius(6);
        button.Content = new Panel
        {
            Width = 24,
            Height = 24,
            Children =
            {
                new StrokeIcon { Data = Geometry.Parse(icon), Width = 24, Height = 24, StrokeThickness = 1.6 },
                new TextBlock { Text = "10", FontSize = 8, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>An on/off button in the app's segment style: the selected fill and a border when on.</summary>
    static Button Toggle(string icon, string text)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { StrokeIcon.Create(icon, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            },
            Height = 28,
            Padding = new Thickness(10, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add("segment");
        AutomationProperties.SetName(button, text);
        return button;
    }

    static void SetOn(Button button, bool on)
    {
        button.Classes.Set("selected", on);
        AutomationProperties.SetItemStatus(button, on ? "On" : "Off");
    }

    /// <summary>Whether Skip idle is on.</summary>
    internal bool SkipsIdle => _skipIdleOn;

    /// <summary>Whether Loop is on.</summary>
    internal bool Loops => s_loop;

    void SetSkipIdle(bool on)
    {
        _skipIdleOn = on;
        SetOn(_skipIdle, on);
        var recorded = RecordedAt(_position);
        MapTimes();
        PlaceTicks();
        Seek(PlayedAt(recorded));
    }

    void ToggleLoop()
    {
        s_loop = !s_loop;
        SetOn(_loop, s_loop);
    }

    /// <summary>The ⋯ menu, built as it opens.</summary>
    List<Control> MoreMenu()
    {
        var show = new MenuItem { Header = "Show in folder" };
        show.Click += (_, _) => ShowInFolderRequested?.Invoke(this, EventArgs.Empty);
        var keys = new MenuItem { Header = "Keyboard shortcuts", InputGesture = new KeyGesture(Key.OemQuestion) };
        keys.Click += (_, _) => ShowKeys(true);
        var delete = new MenuItem { Header = "Delete recording…", Foreground = Dialogs.DangerBrush, InputGesture = new KeyGesture(Key.Delete) };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        return [show, keys, new Separator(), delete];
    }

    /// <summary>The shortcuts card over the picture, opened with ? or the keyboard button.</summary>
    void BuildKeys()
    {
        var close = IconButton(Icons.Close, "Close", "Esc");
        close.Width = close.Height = 28;
        close.Click += (_, _) => ShowKeys(false);
        var card = new Border
        {
            Background = Dialogs.PanelBrush,
            BorderBrush = ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Width = 440,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new DockPanel
            {
                Children =
                {
                    Docked(new DockPanel
                    {
                        Margin = new Thickness(16, 10, 10, 6),
                        Children =
                        {
                            Docked(close, Dock.Right),
                            new TextBlock { Text = "Keyboard shortcuts", FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        },
                    }, Dock.Top),
                    _keyRows,
                },
            },
        };
        AutomationProperties.SetName(card, "Keyboard shortcuts");
        _keys.Children.Add(card);
        _keys.PointerPressed += (_, e) =>
        {
            if (e.Source == _keys)
            {
                ShowKeys(false);
            }
        };
    }

    void ShowKeys(bool show)
    {
        if (show)
        {
            var rows = new List<(string What, string Key)>
            {
                ("Play or pause", "Space"),
                ("Back or forward 10 seconds", "← →"),
                ("Previous or next marker", "Shift+← →"),
                ("Start or end", "Home  End"),
                ("Slower or faster", "[  ]"),
                ("Loop", "L"),
            };
            if (_sound.IsVisible)
            {
                rows.Add(("Mute", "M"));
            }

            if (_findBar.IsVisible)
            {
                rows.Add(("Find in output", "Ctrl+F"));
            }

            rows.Add(("Delete the recording", "Delete"));
            rows.Add(("Show these shortcuts", "?"));
            _keyRows.Children.Clear();
            _keyRows.RowDefinitions.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                _keyRows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var what = new TextBlock { Text = rows[i].What, Foreground = SoftText, Margin = new Thickness(0, 4), VerticalAlignment = VerticalAlignment.Center };
                var key = KeyCap(rows[i].Key);
                Grid.SetRow(what, i);
                Grid.SetRow(key, i);
                Grid.SetColumn(key, 1);
                _keyRows.Children.Add(what);
                _keyRows.Children.Add(key);
            }
        }

        _keys.IsVisible = show;
        if (!show)
        {
            Focus();
        }
    }

    /// <summary>Jumps to the marker before or after where it's playing.</summary>
    void JumpToMarker(int by)
    {
        var times = _entries.Where(e => e.Type == RecordingEventType.Marker).Select(e => e.Played).ToList();
        var to = by > 0
            ? times.Where(t => t > _position + TimeSpan.FromMilliseconds(250)).Cast<TimeSpan?>().FirstOrDefault()
            : times.Where(t => t < _position - TimeSpan.FromMilliseconds(750)).Cast<TimeSpan?>().LastOrDefault();
        if (to is { } at)
        {
            Seek(at);
        }
    }

    void ChangeSpeed(int by) => _speed.SelectedIndex = Math.Clamp(_speed.SelectedIndex + by, 0, Speeds.Length - 1);

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0 && _findBar.IsVisible)
        {
            _find.Focus();
            _find.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _keys.IsVisible)
        {
            ShowKeys(false);
            e.Handled = true;
            return;
        }

        // Leave typing and lists their own keys.
        if (e.Source is TextBox or ComboBox)
        {
            return;
        }

        if (e.KeySymbol == "?")
        {
            ShowKeys(!_keys.IsVisible);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is Key.Left or Key.Right)
        {
            JumpToMarker(e.Key == Key.Right ? 1 : -1);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                Seek(_position - SkipBy);
                break;
            case Key.Right:
                Seek(_position + SkipBy);
                break;
            case Key.Home:
                Seek(TimeSpan.Zero);
                break;
            case Key.M when _sound.IsVisible:
                ToggleMute();
                break;
            case Key.End:
                Seek(_duration);
                break;
            case Key.L:
                ToggleLoop();
                break;
            case Key.OemOpenBrackets:
                ChangeSpeed(-1);
                break;
            case Key.OemCloseBrackets:
                ChangeSpeed(1);
                break;
            case Key.Delete:
                DeleteRequested?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Follows a change to the viewer's color vision or terminal colors in Settings.</summary>
    internal void SetColors(ColorVision vision, ColorCorrection? correction, TerminalColorScheme scheme)
    {
        _vision = vision;
        _correction = correction;
        _scheme = scheme;
        ChooseCorrection();
        if (_terminal is not null)
        {
            _terminal.ColorVision = vision;
            _terminal.ColorScheme = scheme;
        }

        DrawScreen();
    }

    /// <summary>The correction screens are drawn with; tests read it.</summary>
    internal ColorCorrection? Correction => _drawWith;

    void ChooseCorrection()
    {
        var wanted = _info?.ColorCorrection == false ? null : _correction;
        var recordedWith = _info?.CorrectedFor is { } vision ? ColorCorrection.For(vision, _info.CorrectedStrength ?? 100) : null;
        _drawWith = ColorCorrection.Between(recordedWith, wanted);
    }

    // ---- Loading ----

    async Task LoadAsync()
    {
        try
        {
            (_kind, _ending) = await Task.Run(ReadTimeline);
        }
        catch (Exception e) when (e is InvalidDataException or RecordingKeyException or IOException or UnauthorizedAccessException)
        {
            _stage.Children.Clear();
            _stage.Children.Add(new Notice(NoticeKind.Error, e.Message) { MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            return;
        }

        if (_entries.FirstOrDefault(e => e.Type == RecordingEventType.Info) is { Payload: { } info })
        {
            _info = RecordingFile.ReadInfo(info) ?? _info;
        }

        if (_disposed)
        {
            return;
        }

        MapTimes();
        ShowWhat();
        ChooseCorrection();
        PlaceTicks();
        _findBar.IsVisible = _kind == RecordingKind.Terminal;
        _sound.IsVisible = _entries.Any(e => e.Type == RecordingEventType.Audio);
        _banner.Text = _ending switch
        {
            RecordingEnding.Unclosed => "This recording ended without being closed, probably because Orrery stopped while recording.",
            RecordingEnding.Damaged => "Part of this recording couldn't be read, so it plays to where that part starts.",
            _ => string.Empty,
        };
        _banner.IsVisible = !string.IsNullOrEmpty(_banner.Text);
        if (_kind == RecordingKind.Terminal)
        {
            BuildText();
        }
        else
        {
            _reader = RecordingReader.Open(_item.Path, _privateKey);
        }

        _stage.Children.Clear();
        _loaded = true;
        Seek(TimeSpan.Zero);
        Play();
        Focus();
    }

    (RecordingKind, RecordingEnding) ReadTimeline()
    {
        using var reader = RecordingReader.Open(_item.Path, _privateKey);
        bool terminal = reader.Header.Kind == RecordingKind.Terminal;
        foreach (var e in reader.Events())
        {
            // A screen's frames are read again from where they start; only small events are kept.
            bool keep = terminal || e.Type is RecordingEventType.Resize or RecordingEventType.Pointer or RecordingEventType.Marker or RecordingEventType.Info;
            _entries.Add(new Entry(e.Type, e.Time, e.Position, keep ? e.Payload : null));
        }

        return (reader.Header.Kind, reader.Ending);
    }

    /// <summary>The header: the connection, its protocol, when it was recorded, for how long and how big.</summary>
    void ShowWhat()
    {
        _title.Text = _item.Title;
        if (_info is not null)
        {
            _title.Text = _info.Name;
        }

        if (_info is not null && Enum.TryParse<Protocol>(_info.Protocol, ignoreCase: true, out var protocol))
        {
            _protocolText.Text = ProtocolInfo.Label(protocol);
            _protocolText.Foreground = ProtocolStyle.Foreground(protocol);
            _protocol.Background = ProtocolStyle.Background(protocol);
            _protocol.IsVisible = true;
        }
        else if (_info is not null || _entries.Count > 0)
        {
            _protocolText.Text = _info?.Protocol.ToUpperInvariant() ?? (_kind == RecordingKind.Screen ? "SCREEN" : "TERMINAL");
            _protocolText.Foreground = Dialogs.MutedBrush;
            _protocol.Background = Pane.LineBrush;
            _protocol.IsVisible = true;
        }

        var parts = new List<string> { $"Recorded {(_info?.Started.LocalDateTime ?? _item.Modified.ToLocalTime()):f}" };
        if (_entries.Count > 0)
        {
            parts.Add(Length(_entries.Max(e => e.Recorded)));
        }

        parts.Add(_item.Size >= 1 << 20 ? $"{_item.Size / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, _item.Size / 1024)} KB");
        _subtitle.Text = string.Join("  ·  ", parts);
    }

    static string Length(TimeSpan length) => length.TotalHours >= 1 ? $"{(int)length.TotalHours} h {length.Minutes} min"
        : length.TotalMinutes >= 1 ? $"{length.Minutes} min {length.Seconds} s"
        : $"{Math.Max(1, (int)Math.Round(length.TotalSeconds))} s";

    /// <summary>Works out when each event plays: as recorded, or with pauses over 2 seconds cut to 2.</summary>
    void MapTimes()
    {
        bool skip = _skipIdleOn;
        var cut = TimeSpan.Zero;
        var last = TimeSpan.Zero;
        foreach (var entry in _entries)
        {
            if (skip && entry.Recorded - last > ScreenExport.IdleCap)
            {
                cut += entry.Recorded - last - ScreenExport.IdleCap;
            }

            last = entry.Recorded > last ? entry.Recorded : last;
            entry.Played = entry.Recorded - cut;
        }

        _duration = _entries.Count > 0 ? _entries[^1].Played : TimeSpan.Zero;
        _settingTimeline = true;
        _timeline.Maximum = Math.Max(0.1, _duration.TotalSeconds);
        _settingTimeline = false;
    }

    /// <summary>A tick on the timeline for each marker, such as a reconnect, that seeks there when clicked.</summary>
    void PlaceTicks()
    {
        _ticks.Children.Clear();
        double width = _ticks.Bounds.Width - 2 * ThumbInset;
        if (width <= 0 || _duration <= TimeSpan.Zero)
        {
            return;
        }

        foreach (var marker in _entries.Where(e => e.Type == RecordingEventType.Marker))
        {
            var label = $"{Encoding.UTF8.GetString(marker.Payload!)} at {Format(marker.Recorded)}";
            var tick = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(1.5),
                Background = Palette.Warning,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(tick, label);
            AutomationProperties.SetName(tick, $"Marker: {label}");
            var at = marker;
            tick.PointerPressed += (_, e) =>
            {
                Seek(at.Played);
                e.Handled = true;
            };
            Canvas.SetLeft(tick, ThumbInset + width * Math.Clamp(marker.Played / _duration, 0, 1) - 1.5);
            Canvas.SetTop(tick, (_ticks.Bounds.Height - 14) / 2);
            _ticks.Children.Add(tick);
        }

        // Find's matches: short blue ticks under the track, each going to its match.
        for (int i = 0; i < _found.Count && i < MostMatchTicks; i++)
        {
            var entry = _entries[_textEvent[Math.Min(_found[i], _textEvent.Length - 1)]];
            var tick = new Border
            {
                Width = 8,
                Height = 8,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new Border { Width = 2, Height = 6, CornerRadius = new CornerRadius(1), Background = Palette.Accent, VerticalAlignment = VerticalAlignment.Bottom },
            };
            var label = $"Match at {Format(entry.Played)}";
            ToolTip.SetTip(tick, label);
            AutomationProperties.SetName(tick, label);
            int index = i;
            tick.PointerPressed += (_, e) =>
            {
                _foundAt = index - 1;
                FindStep(1);
                e.Handled = true;
            };
            Canvas.SetLeft(tick, ThumbInset + width * Math.Clamp(entry.Played / _duration, 0, 1) - 4);
            Canvas.SetTop(tick, _ticks.Bounds.Height - 8);
            _ticks.Children.Add(tick);
        }
    }

    TimeSpan RecordedAt(TimeSpan played)
    {
        var at = _entries.LastOrDefault(e => e.Played <= played);
        return at is null ? played : at.Recorded + (played - at.Played);
    }

    TimeSpan PlayedAt(TimeSpan recorded)
    {
        var at = _entries.LastOrDefault(e => e.Recorded <= recorded);
        return at is null ? recorded : at.Played + TimeSpan.FromTicks(Math.Min((recorded - at.Recorded).Ticks, ScreenExport.IdleCap.Ticks));
    }

    // ---- Playing ----

    void TogglePlay()
    {
        if (!_loaded)
        {
            return;
        }

        if (_playingNow)
        {
            Pause();
        }
        else
        {
            if (_position >= _duration)
            {
                Seek(TimeSpan.Zero);
            }

            Play();
        }
    }

    void Play()
    {
        _playingNow = true;
        _endNote.IsVisible = false;
        SetPlayLook(playing: true);
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    public void Pause()
    {
        _audio?.Clear();
        _stretch = null;
        _playingNow = false;
        SetPlayLook(playing: false);
        _timer.Stop();
    }

    /// <summary>Pause while playing, play while paused, and play again once it has ended.</summary>
    void SetPlayLook(bool playing)
    {
        bool ended = !playing && _loaded && _duration > TimeSpan.Zero && _position >= _duration;
        _playIcon.Data = Geometry.Parse(playing ? Icons.Pause : ended ? Icons.SkipBack : Icons.Play);
        _playIcon.StrokeThickness = playing ? 2.6 : 2;
        _playIcon.Margin = playing || ended ? default : new Thickness(2, 0, 0, 0); // a triangle looks centered a little right of center
        var name = playing ? "Pause" : ended ? "Play again" : "Play";
        ToolTip.SetTip(_playButton, Tip(name, "Space"));
        AutomationProperties.SetName(_playButton, name);
    }

    void OnTick()
    {
        var now = DateTime.UtcNow;
        var speed = (_speed.SelectedItem as ComboBoxItem)?.Tag as double? ?? 1;
        var to = _position + TimeSpan.FromTicks((long)((now - _lastTick).Ticks * speed));
        _lastTick = now;
        bool ended = to >= _duration;
        if (ended)
        {
            to = _duration;
        }

        // Sound keeps up with the screen, stretched to the speed.
        _audible = speed <= MostAudibleSpeed;
        _playSpeed = speed;
        AdvanceTo(to);
        _audible = false;
        if (ended && s_loop && _duration > TimeSpan.Zero)
        {
            Seek(TimeSpan.Zero);
        }
        else if (ended)
        {
            Pause();
            _endNote.IsVisible = true;
        }
    }

    void Seek(TimeSpan to)
    {
        if (!_loaded)
        {
            return;
        }

        _audio?.Clear();
        _stretch = null;
        _endNote.IsVisible = false;

        to = to < TimeSpan.Zero ? TimeSpan.Zero : to > _duration ? _duration : to;
        if (_kind == RecordingKind.Terminal)
        {
            if (to < _position || _terminal is null)
            {
                NewTerminal();
            }
        }
        else if (to < _position || _cursor is null || KeyFrameBetween(_next, to))
        {
            StartScreenFrom(to);
        }

        AdvanceTo(to);
    }

    void AdvanceTo(TimeSpan to)
    {
        _advancingTo = to;
        bool changed = false;
        while (_next < _entries.Count && _entries[_next].Played <= to)
        {
            changed |= _kind == RecordingKind.Terminal ? PlayTerminal(_entries[_next]) : PlayScreen(_entries[_next]);
            _next++;
        }

        _position = to;
        if (changed && _kind == RecordingKind.Screen)
        {
            DrawScreen();
        }

        _settingTimeline = true;
        _timeline.Value = to.TotalSeconds;
        _settingTimeline = false;
        _time.Text = $"{Format(to)} / {Format(_duration)}";
        if (!_playingNow)
        {
            SetPlayLook(playing: false);
        }
    }

    static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    // ---- Terminal ----

    void NewTerminal()
    {
        _terminalSession = new PlaybackSession();
        _terminal = new TerminalView(_terminalSession) { Focusable = false, ColorVision = _vision, ColorScheme = _scheme };
        _terminalBox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = _terminal };
        _stage.Children.Clear();
        _stage.Children.Add(_terminalBox);
        AutomationProperties.SetName(_terminal, $"Recording of {_title.Text}");
        SetGrid(80, 24);
        _next = 0;
        _position = TimeSpan.Zero;
    }

    void SetGrid(int columns, int rows)
    {
        _terminal!.FixedGrid = (columns, rows);
        var size = _terminal.GridPixelSize;
        _terminal.Width = size.Width;
        _terminal.Height = size.Height;
    }

    bool PlayTerminal(Entry entry)
    {
        switch (entry.Type)
        {
            case RecordingEventType.Output:
                _terminalSession!.Play(entry.Payload!);
                return true;
            case RecordingEventType.Resize when RecordingFile.ReadPair(entry.Payload!) is var (columns, rows) && columns is > 0 and <= 1000 && rows is > 0 and <= 1000:
                SetGrid(columns, rows);
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(\x07|\x1b\\)|\x1b[@-_]|[\x00-\x08\x0b-\x1f\x7f]")]
    private static partial Regex ControlSequences();

    /// <summary>The output as plain text, with which event each character came from, for Find.</summary>
    void BuildText()
    {
        var text = new StringBuilder();
        var owners = new List<int>();
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is { Type: RecordingEventType.Output, Payload: { } payload })
            {
                var chars = new char[payload.Length + 4];
                int count = decoder.GetChars(payload, 0, payload.Length, chars, 0, flush: false);
                var plain = ControlSequences().Replace(new string(chars, 0, count), string.Empty);
                text.Append(plain);
                owners.AddRange(Enumerable.Repeat(i, plain.Length));
            }
        }

        _text = text.ToString();
        _textEvent = owners.ToArray();
    }

    /// <summary>Finds every match as the text changes, and says how many there are.</summary>
    void FindAll()
    {
        _found = [];
        _foundAt = -1;
        var query = _find.Text;
        if (!string.IsNullOrEmpty(query))
        {
            for (int at = _text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase); at >= 0 && _found.Count < MaxMatches;
                at = at + 1 < _text.Length ? _text.IndexOf(query, at + 1, StringComparison.CurrentCultureIgnoreCase) : -1)
            {
                _found.Add(at);
            }
        }

        ShowMatches();
    }

    /// <summary>Goes to the next or previous match, round to the other end, and plays the recording up to it.</summary>
    void FindStep(int by)
    {
        if (_found.Count == 0)
        {
            return;
        }

        _foundAt = _foundAt < 0 ? (by > 0 ? 0 : _found.Count - 1) : (_foundAt + by + _found.Count) % _found.Count;
        int at = _found[_foundAt];
        int last = _textEvent[Math.Min(at + (_find.Text?.Length ?? 1) - 1, _textEvent.Length - 1)];
        Pause();
        Seek(_entries[last].Played);
        ShowMatches();
    }

    void ShowMatches()
    {
        bool any = _found.Count > 0;
        string count = _found.Count >= MaxMatches ? $"{MaxMatches:N0}+" : $"{_found.Count:N0}";
        _matches.Text = string.IsNullOrEmpty(_find.Text) ? string.Empty
            : !any ? "No matches"
            : _foundAt < 0 ? $"{count} found"
            : $"{_foundAt + 1:N0} of {count}";
        _matches.Foreground = !any && !string.IsNullOrEmpty(_find.Text) ? Palette.Warning : Dialogs.MutedBrush;
        _findPrevious.IsEnabled = _findNext.IsEnabled = any;
        PlaceTicks();
    }

    // ---- Screen ----

    void StartScreenFrom(TimeSpan to)
    {
        // The last key frame at or before the point, else the start.
        int start = 0;
        for (int i = 0; i < _entries.Count && _entries[i].Played <= to; i++)
        {
            if (_entries[i].Type == RecordingEventType.KeyFrame)
            {
                start = i;
            }
        }

        _cursor?.Dispose();
        _cursor = _reader!.Events(_entries[start].Position).GetEnumerator();
        _next = start;
        _position = start == 0 ? TimeSpan.Zero : _entries[start].Played;

        // The pointer as it was then, which the key frame doesn't carry.
        for (int i = start - 1; i >= 0; i--)
        {
            if (_entries[i].Type == RecordingEventType.Pointer)
            {
                _decoder.Apply(new RecordedEvent(RecordingEventType.Pointer, _entries[i].Recorded, _entries[i].Payload!, _entries[i].Position));
                break;
            }
        }
    }

    /// <summary>A key frame from entry <paramref name="from"/> up to <paramref name="to"/>: starting there is quicker than playing on.</summary>
    bool KeyFrameBetween(int from, TimeSpan to)
    {
        for (int i = from; i < _entries.Count && _entries[i].Played <= to; i++)
        {
            if (_entries[i].Type == RecordingEventType.KeyFrame)
            {
                return true;
            }
        }

        return false;
    }

    bool PlayScreen(Entry entry)
    {
        if (_cursor is null || !_cursor.MoveNext())
        {
            return false;
        }

        var e = _cursor.Current;
        if (e.Type == RecordingEventType.Audio)
        {
            if (_audible && entry.Played >= _advancingTo - SoundLate)
            {
                PlaySound(e.Payload);
            }

            return false;
        }

        return _decoder.Apply(e) && e.Type is RecordingEventType.KeyFrame or RecordingEventType.Change or RecordingEventType.Pointer;
    }

    void PlaySound(byte[] payload)
    {
        if (RecordingFile.ReadAudio(payload) is not var (rate, channels, pcm))
        {
            return;
        }

        // A format that couldn't be opened isn't tried again until it changes.
        if (_audioFormat != (rate, channels))
        {
            _audio?.Dispose();
            _audio = OpenAudio(rate, channels);
            _audioFormat = (rate, channels);
        }

        var sound = pcm.Span;
        if (_playSpeed == 1)
        {
            _stretch = null;
        }
        else
        {
            if (_stretch is null || _stretch.Speed != _playSpeed || _stretch.SampleRate != rate || _stretch.Channels != channels)
            {
                _stretch = new SoundStretch(rate, channels, _playSpeed);
            }

            sound = _stretch.Stretch(sound);
        }

        _audio?.Write(Louder(sound, s_muted ? 0 : s_volume));
    }

    /// <summary>16-bit samples scaled to the volume; at full volume they're played as recorded.</summary>
    internal static ReadOnlySpan<byte> Louder(ReadOnlySpan<byte> pcm, double volume)
    {
        if (volume >= 1)
        {
            return pcm;
        }

        // Volume sliders feel even when they follow a curve rather than a straight line.
        double gain = volume * volume;
        var scaled = new byte[pcm.Length & ~1];
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            short quieter = (short)Math.Round(sample * gain);
            scaled[i] = (byte)quieter;
            scaled[i + 1] = (byte)(quieter >> 8);
        }

        return scaled;
    }

    void ToggleMute()
    {
        if (s_muted && s_volume == 0)
        {
            _volume.Value = 50; // unmuting at nothing would still be silent
            return;
        }

        s_muted = !s_muted;
        ShowVolume();
    }

    void ShowVolume()
    {
        double shown = s_muted ? 0 : s_volume;
        _speakerIcon.Data = Geometry.Parse(shown == 0 ? Icons.Muted : shown < 0.5 ? Icons.VolumeLow : Icons.Volume);
        var tip = s_muted ? "Unmute" : "Mute";
        ToolTip.SetTip(_speaker, $"{tip} (M)");
        AutomationProperties.SetName(_speaker, tip);
        ToolTip.SetTip(_volume, $"Volume {Math.Round(shown * 100)}%");
        _volume.Opacity = s_muted ? 0.5 : 1;
    }

    void DrawScreen()
    {
        if (!_decoder.HasFrame)
        {
            return;
        }

        var size = new PixelSize(_decoder.Width, _decoder.Height);
        if (_bitmap is null || _bitmap.PixelSize != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
            _stage.Children.Clear();
            _stage.Children.Add(_image);
            AutomationProperties.SetName(_image, $"Recording of {_title.Text}");
        }

        using (var buffer = _bitmap.Lock())
        {
            unsafe
            {
                if (buffer.RowBytes == size.Width * 4)
                {
                    Paint(new Span<byte>((void*)buffer.Address, buffer.RowBytes * size.Height), size.Width, size.Height);
                }
            }
        }

        _image.InvalidateVisual();
    }

    /// <summary>The picture as it is now into RGBA pixels: corrected for the viewer's color vision, then the pointer.</summary>
    unsafe void Paint(Span<byte> pixels, int width, int height)
    {
        _decoder.CopyRgba(pixels);
        if (_drawWith is not null)
        {
            fixed (byte* start = pixels)
            {
                _drawWith.Apply((IntPtr)start, width * 4, width, height, new FrameRect(0, 0, width, height));
            }
        }

        if (_decoder.Pointer is var (x, y))
        {
            PointerArrow.Draw(pixels, width, height, x, y, bytesPerPixel: 4);
        }
    }

    /// <summary>What the screen shows now, as RGBA pixels; tests read it.</summary>
    internal byte[] Picture()
    {
        var pixels = new byte[_decoder.Width * _decoder.Height * 4];
        if (_decoder.HasFrame)
        {
            Paint(pixels, _decoder.Width, _decoder.Height);
        }

        return pixels;
    }

    /// <summary>A terminal session that only plays back: output comes from the recording, and keys go nowhere.</summary>
    sealed class PlaybackSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("recording", 0, null, null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;

        public void Play(byte[] output) => DataReceived?.Invoke(output);
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
