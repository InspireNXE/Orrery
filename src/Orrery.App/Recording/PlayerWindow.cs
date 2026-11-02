using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Recording;
using Orrery.Core.Sessions;

namespace Orrery.App.Recording;

/// <summary>
/// Plays a recording (docs/design-session-recording.md §5.3): a terminal through a read-only terminal view, a screen
/// through the decoder into a bitmap. Play and pause, speed, a timeline with its markers, Skip idle, and Find for a
/// terminal. Seeking back replays a terminal's output from the start, and a screen's from the key frame before.
/// </summary>
sealed partial class PlayerWindow : Window
{
    static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);
    static readonly double[] Speeds = [0.5, 1, 2, 4, 8];

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
    readonly Button _playButton = new() { Content = "Play", MinWidth = 72 };
    readonly Slider _timeline = new() { Minimum = 0, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = Dialogs.MonoFont, FontSize = 12 };
    readonly ComboBox _speed = new() { MinWidth = 70 };
    readonly CheckBox _skipIdle = new() { Content = "Skip idle", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox _markers = new() { MinWidth = 160, PlaceholderText = "Jump to…" };
    readonly TextBox _find = new() { Watermark = "Find in output", MinWidth = 160 };
    readonly Button _findNext = new() { Content = "Find" };
    readonly TextBlock _banner = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Dialogs.WarningBrush, IsVisible = false };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Dialogs.MutedBrush };
    RecordingKind _kind;
    RecordingEnding _ending;
    TimeSpan _duration;
    TimeSpan _position;
    DateTime _lastTick;
    bool _playingNow;
    bool _settingTimeline;
    int _next;

    // Terminal playback.
    PlaybackSession? _terminalSession;
    TerminalView? _terminal;
    Viewbox? _terminalBox;
    string _text = string.Empty;
    int[] _textEvent = [];
    int _findFrom;

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
        Title = $"{item.Title} — recording";
        Width = 1000;
        Height = 700;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        foreach (var speed in Speeds)
        {
            _speed.Items.Add(new ComboBoxItem { Content = $"{speed:0.#}×", Tag = speed });
        }

        _speed.SelectedIndex = 1;
        AutomationProperties.SetName(_speed, "Speed");
        AutomationProperties.SetName(_timeline, "Position");
        AutomationProperties.SetName(_markers, "Jump to a marker");
        AutomationProperties.SetName(_find, "Find in output");
        _playButton.Classes.Add("accent");
        _playButton.Click += (_, _) => TogglePlay();
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
            Seek(PlayedAt(recorded));
        };
        _markers.SelectionChanged += (_, _) =>
        {
            if (_markers.SelectedItem is ComboBoxItem { Tag: Entry marker })
            {
                Seek(marker.Played);
            }
        };
        _findNext.Click += (_, _) => FindNext();
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                FindNext();
                e.Handled = true;
            }
        };
        var export = new Button { Content = "Export…" };
        export.Click += async (_, _) =>
        {
            Pause();
            await RecordingExport.ExportAsync(this, _service, _item.Path, _privateKey, Path.GetFileNameWithoutExtension(_item.Path));
        };

        var controls = new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _playButton, _time } }, Dock.Left),
                Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 0, 0, 0), Children = { _speed, _skipIdle } }, Dock.Right),
                _timeline,
            },
        };
        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _markers, _find, _findNext, export },
        };

        _stage.Children.Add(new TextBlock { Text = "Reading the recording…", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                Docked(new StackPanel { Children = { controls, tools, _banner, _status } }, Dock.Bottom),
                _stage,
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
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Space && e.Source is not TextBox)
            {
                TogglePlay();
                e.Handled = true;
            }
        };
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
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
            _status.Text = e.Message;
            return;
        }

        MapTimes();
        foreach (var marker in _entries.Where(e => e.Type == RecordingEventType.Marker))
        {
            _markers.Items.Add(new ComboBoxItem { Content = $"{Format(marker.Recorded)}  {Encoding.UTF8.GetString(marker.Payload!)}", Tag = marker });
        }

        _markers.IsVisible = _markers.Items.Count > 0;
        _find.IsVisible = _findNext.IsVisible = _kind == RecordingKind.Terminal;
        _banner.Text = _ending switch
        {
            RecordingEnding.Unclosed => "This recording ended without being closed, probably because Orrery stopped while recording.",
            RecordingEnding.Damaged => "Part of this recording couldn't be read, so it plays to where that part starts.",
            _ => string.Empty,
        };
        _banner.IsVisible = _banner.Text.Length > 0;
        if (_kind == RecordingKind.Terminal)
        {
            BuildText();
        }
        else
        {
            _reader = RecordingReader.Open(_item.Path, _privateKey);
        }

        _stage.Children.Clear();
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
        _playButton.Content = "Pause";
        _lastTick = DateTime.UtcNow;
        _timer.Start();
    }

    void Pause()
    {
        _playingNow = false;
        _playButton.Content = "Play";
        _timer.Stop();
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
        AutomationProperties.SetName(_terminal, $"Recording of {_item.Title}");
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

    void FindNext()
    {
        var query = _find.Text;
        if (string.IsNullOrEmpty(query) || _text.Length == 0)
        {
            return;
        }

        int at = _text.IndexOf(query, _findFrom, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0 && _findFrom > 0)
        {
            at = _text.IndexOf(query, 0, StringComparison.CurrentCultureIgnoreCase);
        }

        if (at < 0)
        {
            _status.Text = $"\"{query}\" isn't in this recording.";
            return;
        }

        _findFrom = at + 1;
        int last = _textEvent[Math.Min(at + query.Length - 1, _textEvent.Length - 1)];
        Pause();
        Seek(_entries[last].Played);
        _status.Text = $"Found \"{query}\" at {Format(_entries[last].Recorded)}.";
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
            AutomationProperties.SetName(_image, $"Recording of {_item.Title}");
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
