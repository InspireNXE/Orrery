using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Recording;

namespace Orrery.App.Recording;

/// <summary>A recording in the list: what its file says before it's opened, and what its first event says after.</summary>
sealed record RecordingItem(string Path, long Size, DateTime Modified, RecordingKind? Kind, RecordingInfo? Info, string? Problem)
{
    public string Title => Info?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public DateTime Started => Info?.Started.LocalDateTime ?? Modified.ToLocalTime();

    /// <summary>From its start to the file's last write, which is when it stopped once it's closed.</summary>
    public TimeSpan? Length => Info is null ? null : Modified.ToLocalTime() - Info.Started.LocalDateTime is { Ticks: >= 0 } length ? length : null;

    public string SizeText => Size >= 1 << 20 ? $"{Size / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, Size / 1024)} KB";

    public string Badge => Info?.Protocol.ToUpperInvariant() ?? (Kind == RecordingKind.Screen ? "SCREEN" : Kind == RecordingKind.Terminal ? "TERMINAL" : "?");

    public override string ToString() => $"{Title}, {Badge}, {Started:g}, {SizeText}{(Problem is null ? "" : $", {Problem}")}";
}

/// <summary>
/// The side panel listing recordings in the data folder, newest first (docs/design-session-recording.md §5.3).
/// Clicking one or pressing Enter plays it in <see cref="Stage"/>, the section's main area; the context menu exports, shows it in the folder or deletes it. Names show once
/// the credential store is open, since only the recordings key can read them; until then each is its date, kind and size.
/// </summary>
sealed class RecordingsPanel : DockPanel
{
    public const string Note = "Recordings hold everything the session showed: what's on screen, and every line a terminal printed. "
        + "What you type isn't recorded. They're encrypted, and play only in Orrery with this data folder's saved credentials. "
        + "The remote side isn't told it's being recorded; recording other people's sessions may need their agreement.";

    const string EmptyNote = "Sessions you record show up here. Turn on Record sessions for a connection, or start one from a tab's menu.";

    readonly RecordingService _service;
    readonly ListBox _list = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    readonly TextBox _filter = new() { PlaceholderText = "Filter", FontSize = 12, MinHeight = 30, Margin = new Thickness(12, 0, 12, 10) };
    readonly TextBlock _empty = Pane.EmptyText(EmptyNote, side: true);
    readonly TextBlock _total = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, Margin = new Thickness(14, 6, 14, 10) };
    readonly Notice _status = new(NoticeKind.Info) { Margin = new Thickness(14, 0, 14, 8), IsVisible = false };
    readonly Button _unlock = new() { Content = "Unlock", FontSize = 12, MinHeight = 0, Height = 26, Padding = new Thickness(10, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _locked;

    // What each file says, kept while the file is unchanged, so refreshing doesn't decrypt every one again.
    readonly Dictionary<string, RecordingItem> _described = new(StringComparer.OrdinalIgnoreCase);
    List<RecordingItem> _items = [];
    int _loading;

    /// <summary>The header row, which the main window adds its pin button to.</summary>
    public DockPanel Header { get; }

    public RecordingsPanel(RecordingService service)
    {
        _service = service;
        AutomationProperties.SetName(_list, "Recordings");
        AutomationProperties.SetName(_filter, "Filter recordings");
        _filter.InnerLeftContent = new StrokeIcon { Data = Geometry.Parse("M4 6h16 M7 12h10 M10 18h4"), Width = 13, Height = 13, StrokeThickness = 2, Margin = new Thickness(10, 0, 0, 0), Foreground = Dialogs.MutedBrush };
        _filter.TextChanged += (_, _) => Show();

        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.ItemTemplate = new FuncDataTemplate<RecordingItem>((r, _) => r is null ? new Panel() : Row(r));
        _list.Tapped += async (_, _) => await PlayAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await PlayAsync();
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                await DeleteAsync();
            }
        };

        var play = new MenuItem { Header = "Play" };
        play.Click += async (_, _) => await PlayAsync();
        var export = new MenuItem { Header = "Export…" };
        export.Click += async (_, _) => await ExportAsync();
        var show = new MenuItem { Header = "Show in folder" };
        show.Click += (_, _) => ShowInFolder();
        var delete = new MenuItem { Header = "Delete…" };
        delete.Click += async (_, _) => await DeleteAsync();
        _list.ContextMenu = new ContextMenu { Items = { play, export, show, new Separator(), delete } };
        _list.ContextMenu.Opening += (_, e) =>
        {
            e.Cancel = Selected is null;
            play.IsEnabled = export.IsEnabled = Selected is { Problem: null };
        };

        Stage.ShowInFolderRequested += path => Reveal(path, select: true);
        Stage.DeleteRequested += async path =>
        {
            if (_items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                await DeleteAsync(item);
            }
        };

        var folder = new Button { Content = StrokeIcon.Create(Icons.Folder, 16), Width = 28, Height = 28 };
        folder.Classes.Add("icon");
        ToolTip.SetTip(folder, "Open the recordings folder");
        AutomationProperties.SetName(folder, "Open the recordings folder");
        folder.Click += (_, _) => OpenFolder();

        _unlock.Click += async (_, _) =>
        {
            if (Owner is { } owner && await Credentials.VaultUnlockWindow.EnsureUnlockedAsync(_service.Credentials, owner))
            {
                Refresh();
            }
        };
        _locked = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(14, 0, 14, 10),
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Unlock the vault to see what each recording is, and to play them.", FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap },
                _unlock,
            },
        };

        Children.Add(Pane.Docked(Header = new DockPanel
        {
            Margin = new Thickness(14, 10, 10, 6),
            Height = 28,
            Children =
            {
                Pane.Docked(folder, Dock.Right),
                new TextBlock { Text = "RECORDINGS", Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center },
            },
        }, Dock.Top));
        Children.Add(Pane.Docked(_filter, Dock.Top));
        Children.Add(Pane.Docked(_locked, Dock.Top));
        Children.Add(Pane.Docked(_status, Dock.Top));
        Children.Add(Pane.Docked(_total, Dock.Bottom));
        Children.Add(new Panel { Children = { _list, _empty } });

        _service.Changed += (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                Refresh();
            }
        };
    }

    /// <summary>Where the chosen recording plays: the Recordings section's main area.</summary>
    public RecordingStage Stage { get; } = new();

    /// <summary>The text in the filter, such as a connection's name to show only its recordings.</summary>
    public string Filter
    {
        get => _filter.Text ?? string.Empty;
        set => _filter.Text = value;
    }

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    RecordingItem? Selected => _list.SelectedItem as RecordingItem;

    /// <summary>Reads the folder again, and what each new or changed file says if the store is open.</summary>
    public async void Refresh()
    {
        int loading = ++_loading;
        bool locked = _service.Credentials.IsLocked;
        if (locked)
        {
            // What the files say stays behind the lock, as the saved credentials do.
            _described.Clear();
        }

        byte[]? key = null;
        string? problem = null;
        try
        {
            key = locked ? null : await RecordingKeys.PrivateKeyAsync(_service.Credentials);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
        {
            problem = e.Message;
        }

        List<RecordingItem> items;
        try
        {
            items = await Task.Run(() => RecordingRetention.List(_service.Folder).Select(f => Describe(f, key)).ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            items = [];
            problem = $"Couldn't read the recordings folder: {e.Message}";
        }
        finally
        {
            if (key is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }

        if (loading != _loading)
        {
            return;
        }

        _items = items;
        _locked.IsVisible = locked && items.Count > 0;
        _status.Text = problem ?? (!locked && key is null && items.Count > 0
            ? "There's no recordings key with these saved credentials, so these recordings can't be played here."
            : null);
        _status.Kind = problem is null ? NoticeKind.Info : NoticeKind.Warning;
        _status.IsVisible = !string.IsNullOrEmpty(_status.Text);
        Show();
    }

    /// <summary>The rows that match the filter, keeping the selection.</summary>
    void Show()
    {
        var selected = Selected?.Path;
        var filter = Filter.Trim();
        var shown = _items.Where(i => filter.Length == 0
            || i.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || i.Badge.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = shown;
        _list.SelectedItem = shown.FirstOrDefault(i => i.Path == selected);
        _list.IsVisible = shown.Count > 0;
        _empty.IsVisible = shown.Count == 0;
        _empty.Text = _items.Count == 0 ? EmptyNote : $"No recordings match \"{filter}\".";
        _filter.IsVisible = _items.Count > 0 || filter.Length > 0;
        long bytes = _items.Sum(i => i.Size);
        _total.Text = _items.Count == 0 ? string.Empty
            : $"{_items.Count} recording{(_items.Count == 1 ? "" : "s")} · {(bytes >= 1 << 30 ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB" : bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB")}";
        _total.IsVisible = _items.Count > 0;
    }

    Control Row(RecordingItem r)
    {
        bool recording = _service.IsRecording(r.Path);
        var protocol = r.Info is { } info && Enum.TryParse<Protocol>(info.Protocol, ignoreCase: true, out var p) ? p : (Protocol?)null;
        var detail = new List<string> { When(r.Started) };
        if (recording)
        {
            detail.Add("recording now");
        }
        else if (r.Length is { } length)
        {
            detail.Add(Length(length));
        }

        detail.Add(r.SizeText);
        if (r.Problem is not null)
        {
            detail.Add(r.Problem);
        }

        // A dock panel, not a horizontal stack, so the name gets only the room left of the badge and ends in "…".
        var title = new DockPanel
        {
            Children = { new TextBlock { Text = r.Title, TextTrimming = TextTrimming.CharacterEllipsis } },
        };
        if (recording)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = SessionTab.RecordingBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            ToolTip.SetTip(dot, "Being recorded now");
            title.Children.Insert(0, Pane.Docked(dot, Dock.Left));
        }

        return new DockPanel
        {
            Margin = new Thickness(0, 4),
            Opacity = r.Problem is null ? 1 : 0.6,
            Children =
            {
                Pane.Docked(new Border
                {
                    Background = protocol is { } badge ? ProtocolStyle.Background(badge) : Pane.LineBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(5, 1),
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = protocol is { } label ? ProtocolInfo.Label(label) : r.Badge,
                        FontSize = 10,
                        FontWeight = FontWeight.Medium,
                        FontFamily = Dialogs.MonoFont,
                        Foreground = protocol is { } fore ? ProtocolStyle.Foreground(fore) : Dialogs.MutedBrush,
                    },
                }, Dock.Right),
                new StackPanel
                {
                    Children =
                    {
                        title,
                        new TextBlock { Text = string.Join(" · ", detail), FontSize = 11, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    static string When(DateTime started) =>
        started.Date == DateTime.Today ? $"Today {started:t}"
        : started.Date == DateTime.Today.AddDays(-1) ? $"Yesterday {started:t}"
        : started.Year == DateTime.Today.Year ? $"{started:d MMM} {started:t}"
        : started.ToString("d MMM yyyy");

    static string Length(TimeSpan length) => length.TotalHours >= 1 ? $"{(int)length.TotalHours} h {length.Minutes} min"
        : length.TotalMinutes >= 1 ? $"{(int)length.TotalMinutes} min"
        : $"{Math.Max(1, (int)length.TotalSeconds)} s";

    RecordingItem Describe(FileInfo file, byte[]? key)
    {
        var modified = file.LastWriteTimeUtc;
        if (key is not null && _described.TryGetValue(file.FullName, out var known) && known.Size == file.Length && known.Modified == modified)
        {
            return known;
        }

        RecordingItem item;
        try
        {
            var header = RecordingReader.ReadHeader(file.FullName);
            if (key is null)
            {
                return new RecordingItem(file.FullName, file.Length, modified, header.Kind, null, null);
            }

            using var reader = RecordingReader.Open(file.FullName, key);
            var first = reader.Events().FirstOrDefault();
            var info = first?.Type == RecordingEventType.Info ? RecordingFile.ReadInfo(first.Payload) : null;
            item = new RecordingItem(file.FullName, file.Length, modified, header.Kind, info, info is null ? "can't be read" : null);
        }
        catch (RecordingKeyException)
        {
            item = new RecordingItem(file.FullName, file.Length, modified, null, null, "made with another key");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            item = new RecordingItem(file.FullName, file.Length, modified, null, null, "not a recording Orrery can read");
        }

        lock (_described)
        {
            _described[file.FullName] = item;
        }

        return item;
    }

    async Task PlayAsync()
    {
        if (Selected is { Problem: null } item && !string.Equals(Stage.Player?.Path, item.Path, StringComparison.OrdinalIgnoreCase)
            && Owner is { } owner && await _service.PrivateKeyAsync(owner) is { } key)
        {
            // Opening it may have unlocked the store, which shows the names.
            Refresh();
            Stage.Play(_service, item, key);
        }
    }

    async Task ExportAsync()
    {
        if (Selected is { Problem: null } item && Owner is { } owner && await _service.PrivateKeyAsync(owner) is { } key)
        {
            try
            {
                await RecordingExport.ExportAsync(owner, _service, item.Path, key, System.IO.Path.GetFileNameWithoutExtension(item.Path));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    void ShowInFolder()
    {
        if (Selected is { } item)
        {
            Reveal(item.Path, select: true);
        }
    }

    void OpenFolder()
    {
        Directory.CreateDirectory(_service.Folder);
        Reveal(_service.Folder, select: false);
    }

    void Reveal(string path, bool select)
    {
        try
        {
            if (select && OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add($"/select,{path}");
                Process.Start(start);
            }
            else
            {
                Process.Start(new ProcessStartInfo(select ? System.IO.Path.GetDirectoryName(path)! : path) { UseShellExecute = true });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            ShowProblem($"Couldn't open the folder: {e.Message}");
        }
    }

    async Task DeleteAsync()
    {
        if (Selected is { } item)
        {
            await DeleteAsync(item);
        }
    }

    async Task DeleteAsync(RecordingItem item)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        // One playing here is let go of first; one being made has to be stopped.
        if (_service.IsRecording(item.Path))
        {
            ShowProblem("That recording is being made. Stop it first.");
            return;
        }

        if (!await Dialogs.ConfirmAsync(owner, "Delete recording", $"Delete \"{item.Title}\" from {item.Started:g} for good? It can't be got back.", "Delete"))
        {
            return;
        }

        Stage.Close(item.Path);
        if (_service.InUse.Contains(item.Path))
        {
            ShowProblem("That recording is in use. Stop it first.");
            return;
        }

        try
        {
            File.Delete(item.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowProblem($"Couldn't delete it: {e.Message}");
            return;
        }

        Refresh();
    }

    void ShowProblem(string text)
    {
        _status.Kind = NoticeKind.Warning;
        _status.Text = text;
        _status.IsVisible = true;
    }
}
