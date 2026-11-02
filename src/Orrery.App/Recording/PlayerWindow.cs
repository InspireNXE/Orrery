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

namespace Orrery.App.Recording;

/// <summary>
/// Plays a recording (docs/design-session-recording.md §5.3): a terminal through a read-only terminal view, a screen
/// through the decoder into a bitmap. A header says what was recorded and when, with Find for a terminal and Export;
/// under the picture are the timeline with its markers, play and pause, 10 seconds back and forward, speed and Skip
/// idle. Seeking back replays a terminal's output from the start, and a screen's from the key frame before.
/// </summary>
sealed partial class PlayerWindow : Window
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);
    static readonly TimeSpan SkipBy = TimeSpan.FromSeconds(10);
    static readonly double[] Speeds = [0.5, 1, 2, 4, 8];
    static readonly IBrush StageBorder = new SolidColorBrush(Color.Parse("#2A2F38")).ToImmutable();

    // Half the slider's thumb: the track's ends sit this far in from the control's edges.
    const double ThumbInset = 10;
    const int MaxMatches = 10_000;

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

    // Header.
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _protocolText = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _protocol = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Notice _banner = new(NoticeKind.Warning) { IsVisible = false, Margin = new Thickness(16, 10, 16, 0) };

    // Find, for a terminal.
    readonly TextBox _find = new() { Watermark = "Find in output", Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
    readonly TextBlock _matches = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Right };
    readonly Button _findPrevious = IconButton(Icons.Previous, "Previous match (Shift+Enter)");
    readonly Button _findNext = IconButton(Icons.Next, "Next match (Enter)");
    readonly StackPanel _findBar = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };

    // Transport.
    readonly Button _playButton = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StrokeIcon _playIcon = StrokeIcon.Create(Icons.Play, 18);
    readonly Slider _timeline = new() { Minimum = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    readonly Canvas _ticks = new() { IsHitTestVisible = true };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = Dialogs.MonoFont, FontSize = 12, Margin = new Thickness(14, 0, 0, 0) };
    readonly ComboBox _speed = new() { MinWidth = 76, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _skipIdle = new() { Content = "Skip idle", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    RecordingKind _kind;
    RecordingEnding _ending;
    RecordingInfo? _info;
    TimeSpan _duration;
    TimeSpan _position;
    DateTime _lastTick;
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

    public PlayerWindow(RecordingService service, RecordingItem item, byte[] privateKey)
    {
        _service = service;
        _item = item;
        _privateKey = privateKey;
        _playing = service.Playing(item.Path);
        _info = item.Info;
        Width = 1000;
        Height = 720;
        MinWidth = 640;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;
        ShowWhat();

        foreach (var speed in Speeds)
        {
            _speed.Items.Add(new ComboBoxItem { Content = $"{speed:0.#}×", Tag = speed });
        }

        _speed.SelectedIndex = 1;
        ToolTip.SetTip(_speed, "Playback speed");
        AutomationProperties.SetName(_speed, "Playback speed");
        AutomationProperties.SetName(_timeline, "Position");
        AutomationProperties.SetName(_find, "Find in output");
        ToolTip.SetTip(_skipIdle, "Cut pauses longer than 2 seconds down to 2");

        _playButton.Classes.Add("accent");
        _playButton.Content = _playIcon;
        _playButton.Click += (_, _) => TogglePlay();
        SetPlayLook(playing: false);
        var back = SkipButton(Icons.SkipBack, "Back 10 seconds (Left)", () => Seek(_position - SkipBy));
        var forward = SkipButton(Icons.SkipForward, "Forward 10 seconds (Right)", () => Seek(_position + SkipBy));

        _timeline.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !_settingTimeline)
            {
                Seek(TimeSpan.FromSeconds(_timeline.Value));
            }
        };
        _skipIdle.IsCheckedChanged += (_, _) =>
        {
            var recorded = RecordedAt(_position);
            MapTimes();
            PlaceTicks();
            Seek(PlayedAt(recorded));
        };

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
        _findBar.Children.Add(new Panel
        {
            Children =
            {
                _find,
                new StrokeIcon { Data = Geometry.Parse(Icons.Search), Width = 14, Height = 14, Foreground = Dialogs.MutedBrush, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 9, 0) },
            },
        });
        _findBar.Children.Add(_matches);
        _findBar.Children.Add(_findPrevious);
        _findBar.Children.Add(_findNext);

        var export = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { StrokeIcon.Create(Icons.Download, 14), new TextBlock { Text = "Export…", VerticalAlignment = VerticalAlignment.Center } },
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        ToolTip.SetTip(export, "Save an unencrypted copy to share: asciicast for a terminal, video for a screen");
        AutomationProperties.SetName(export, "Export");
        export.Click += async (_, _) =>
        {
            Pause();
            await RecordingExport.ExportAsync(this, _service, _item.Path, _privateKey, Path.GetFileNameWithoutExtension(_item.Path));
        };

        _protocol.Child = _protocolText;
        var header = new Border
        {
            BorderBrush = Pane.LineBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 12),
            Child = new DockPanel
            {
                Children =
                {
                    Docked(export, Dock.Right),
                    Docked(_findBar, Dock.Right),
                    new StackPanel
                    {
                        Spacing = 3,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _protocol, _title } },
                            _subtitle,
                        },
                    },
                },
            },
        };

        var timeline = new Grid { Children = { _timeline, _ticks } };
        timeline.SizeChanged += (_, _) => PlaceTicks();
        var transport = new DockPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Children =
            {
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { back, _playButton, forward, _time } }, Dock.Left),
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Right, Children = { _skipIdle, _speed } }, Dock.Right),
                new Panel(),
            },
        };

        _stage.Children.Add(new TextBlock { Text = "Reading the recording…", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Content = new DockPanel
        {
            Children =
            {
                Docked(header, Dock.Top),
                Docked(_banner, Dock.Top),
                Docked(new StackPanel { Margin = new Thickness(16, 4, 16, 12), Children = { timeline, transport } }, Dock.Bottom),
                new Border
                {
                    Margin = new Thickness(16, 12, 16, 4),
                    BorderBrush = StageBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    ClipToBounds = true,
                    Child = _stage,
                },
            },
        };

        _timer.Tick += (_, _) => OnTick();
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cursor?.Dispose();
            _reader?.Dispose();
            _playing.Dispose();
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_privateKey);
        };

        // Tunnel, so Space plays and pauses wherever focus is rather than pressing the focused button.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    static Button IconButton(string icon, string tip)
    {
        var button = new Button { Content = StrokeIcon.Create(icon, 16), Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip[..(tip.IndexOf(" (", StringComparison.Ordinal) is var at and >= 0 ? at : tip.Length)]);
        return button;
    }

    /// <summary>A round arrow with "10" inside it.</summary>
    static Button SkipButton(string icon, string tip, Action click)
    {
        var button = IconButton(icon, tip);
        button.Width = button.Height = 38;
        button.Content = new Panel
        {
            Width = 26,
            Height = 26,
            Children =
            {
                new StrokeIcon { Data = Geometry.Parse(icon), Width = 26, Height = 26, StrokeThickness = 1.6 },
                new TextBlock { Text = "10", FontSize = 8, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        button.Click += (_, _) => click();
        return button;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) != 0 && _findBar.IsVisible)
        {
            _find.Focus();
            _find.SelectAll();
            e.Handled = true;
            return;
        }

        // Leave typing, lists and check boxes their own keys.
        if (e.Source is TextBox or ComboBox or CheckBox || e.KeyModifiers != KeyModifiers.None)
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
            case Key.End:
                Seek(_duration);
                break;
            default:
                return;
        }

        e.Handled = true;
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

        MapTimes();
        ShowWhat();
        PlaceTicks();
        _findBar.IsVisible = _kind == RecordingKind.Terminal;
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

    /// <summary>The header and window title: the connection, its protocol, when it was recorded, for how long and how big.</summary>
    void ShowWhat()
    {
        _title.Text = _item.Title;
        if (_info is not null)
        {
            _title.Text = _info.Name;
        }

        Title = $"{_title.Text} — recording";
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
        bool skip = _skipIdle.IsChecked == true;
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
        SetPlayLook(playing: true);
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    void Pause()
    {
        _playingNow = false;
        SetPlayLook(playing: false);
        _timer.Stop();
    }

    void SetPlayLook(bool playing)
    {
        _playIcon.Data = Geometry.Parse(playing ? Icons.Pause : Icons.Play);
        _playIcon.StrokeThickness = playing ? 2.6 : 2;
        _playIcon.Margin = playing ? default : new Thickness(2, 0, 0, 0); // a triangle looks centered a little right of center
        var name = playing ? "Pause (Space)" : "Play (Space)";
        ToolTip.SetTip(_playButton, name);
        AutomationProperties.SetName(_playButton, playing ? "Pause" : "Play");
    }

    void OnTick()
    {
        var now = DateTime.UtcNow;
        var speed = (_speed.SelectedItem as ComboBoxItem)?.Tag as double? ?? 1;
        var to = _position + TimeSpan.FromTicks((long)((now - _lastTick).Ticks * speed));
        _lastTick = now;
        if (to >= _duration)
        {
            to = _duration;
            Pause();
        }

        AdvanceTo(to);
    }

    void Seek(TimeSpan to)
    {
        if (!_loaded)
        {
            return;
        }

        to = to < TimeSpan.Zero ? TimeSpan.Zero : to > _duration ? _duration : to;
        if (_kind == RecordingKind.Terminal)
        {
            if (to < _position || _terminal is null)
            {
                NewTerminal();
            }
        }
        else if (to < _position || _cursor is null || to - _position > TimeSpan.FromSeconds(30))
        {
            StartScreenFrom(to);
        }

        AdvanceTo(to);
    }

    void AdvanceTo(TimeSpan to)
    {
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
    }

    static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    // ---- Terminal ----

    void NewTerminal()
    {
        _terminalSession = new PlaybackSession();
        _terminal = new TerminalView(_terminalSession) { Focusable = false };
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

    bool PlayScreen(Entry entry)
    {
        if (_cursor is null || !_cursor.MoveNext())
        {
            return false;
        }

        var e = _cursor.Current;
        return _decoder.Apply(e) && e.Type is RecordingEventType.KeyFrame or RecordingEventType.Change or RecordingEventType.Pointer;
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
                var pixels = new Span<byte>((void*)buffer.Address, buffer.RowBytes * size.Height);
                if (buffer.RowBytes == size.Width * 4)
                {
                    _decoder.CopyRgba(pixels);
                    if (_decoder.Pointer is var (x, y))
                    {
                        PointerArrow.Draw(pixels, size.Width, size.Height, x, y, bytesPerPixel: 4);
                    }
                }
            }
        }

        _image.InvalidateVisual();
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
