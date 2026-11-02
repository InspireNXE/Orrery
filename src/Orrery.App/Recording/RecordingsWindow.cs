using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Recording;

namespace Orrery.App.Recording;

/// <summary>A recording in the list: what its file says before it's opened, and what its first event says after.</summary>
sealed record RecordingItem(string Path, long Size, DateTime Modified, RecordingKind? Kind, RecordingInfo? Info, string? Problem)
{
    public string Title => Info?.Name ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Detail
    {
        get
        {
            var kind = Info?.Protocol ?? (Kind == RecordingKind.Screen ? "Screen" : Kind == RecordingKind.Terminal ? "Terminal" : "?");
            var when = (Info?.Started.LocalDateTime ?? Modified.ToLocalTime()).ToString("g");
            var size = Size >= 1 << 20 ? $"{Size / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, Size / 1024)} KB";
            return Problem is null ? $"{kind} · {when} · {size}" : $"{kind} · {when} · {size} · {Problem}";
        }
    }

    public override string ToString() => $"{Title}, {Detail}";
}

/// <summary>
/// The recordings in the data folder, newest first, with Play, Export, Show in folder and Delete
/// (docs/design-session-recording.md §5.3). Names and times show once the credential store is open.
/// </summary>
sealed class RecordingsWindow : Window
{
    public const string Note = "Recordings hold everything the session showed: what's on screen, and every line a terminal printed. "
        + "What you type isn't recorded. They're encrypted, and play only in Orrery with this data folder's saved credentials. "
        + "The remote side isn't told it's being recorded; recording other people's sessions may need their agreement.";

    readonly RecordingService _service;
    readonly ListBox _list = new() { MinHeight = 240 };
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Dialogs.MutedBrush };
    readonly CheckBox _onlyThis;
    readonly string? _connection;
    readonly Button _play = new() { Content = "Play" };
    readonly Button _export = new() { Content = "Export…" };
    readonly Button _show = new() { Content = "Show in folder" };
    readonly Button _delete = new() { Content = "Delete…" };
    byte[]? _privateKey;

    public RecordingsWindow(RecordingService service, string? connection = null)
    {
        _service = service;
        _connection = connection;
        Title = "Recordings";
        Width = 640;
        Height = 520;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Dialogs.PanelBrush;

        _onlyThis = new CheckBox { Content = $"Only {connection}", IsChecked = connection is not null, IsVisible = connection is not null };
        _onlyThis.IsCheckedChanged += (_, _) => _ = LoadAsync();
        AutomationProperties.SetName(_list, "Recordings");
        _play.Classes.Add("accent");
        _delete.Foreground = Dialogs.DangerBrush;
        _play.Click += async (_, _) => await PlayAsync();
        _list.DoubleTapped += async (_, _) => await PlayAsync();
        _export.Click += async (_, _) => await ExportAsync();
        _show.Click += (_, _) => ShowInFolder();
        _delete.Click += async (_, _) => await DeleteAsync();
        _list.SelectionChanged += (_, _) => UpdateButtons();

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Docked(new StackPanel
                {
                    Spacing = 8,
                    Margin = new Thickness(0, 0, 0, 12),
                    Children =
                    {
                        Dialogs.Heading(new TextBlock { Text = "Recordings", FontSize = 16, FontWeight = FontWeight.SemiBold }),
                        _onlyThis,
                    },
                }, Dock.Top),
                Docked(new StackPanel
                {
                    Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0),
                    Children =
                    {
                        _status,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _play, _export, _show, _delete } },
                    },
                }, Dock.Bottom),
                _list,
            },
        };

        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            if (_privateKey is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(_privateKey);
            }
        };
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    RecordingItem? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as RecordingItem;

    void UpdateButtons()
    {
        bool any = Selected is not null;
        bool readable = Selected is { Problem: null } && _privateKey is not null;
        _play.IsEnabled = readable;
        _export.IsEnabled = readable;
        _show.IsEnabled = any;
        _delete.IsEnabled = any;
    }

    async Task LoadAsync()
    {
        try
        {
            _privateKey ??= await _service.PrivateKeyAsync(this);
        }
        catch (InvalidDataException e)
        {
            _status.Text = e.Message;
        }

        var key = _privateKey;
        var only = _onlyThis.IsChecked == true ? _connection : null;
        var items = await Task.Run(() => RecordingRetention.List(_service.Folder).Select(f => Describe(f, key)).ToList());
        _list.Items.Clear();
        foreach (var item in items.Where(i => only is null || i.Info?.Name == only))
        {
            var row = new ListBoxItem
            {
                Tag = item,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(4, 2),
                    Children =
                    {
                        new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = item.Detail, FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            AutomationProperties.SetName(row, item.ToString());
            _list.Items.Add(row);
        }

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        _status.Text = (_privateKey, items.Count) switch
        {
            (_, 0) => "No recordings yet. Turn on Record sessions for a connection, or start one from a tab's menu.",
            (null, _) when _service.Credentials.IsLocked => "Unlock the vault to see what each recording is, and to play them.",
            (null, _) => "There's no recordings key with these saved credentials, so these recordings can't be played here.",
            _ => $"{items.Count} recording{(items.Count == 1 ? "" : "s")}, {items.Sum(i => i.Size) / (1024.0 * 1024):0.#} MB.",
        };
        UpdateButtons();
    }

    static RecordingItem Describe(FileInfo file, byte[]? key)
    {
        try
        {
            var header = RecordingReader.ReadHeader(file.FullName);
            if (key is null)
            {
                return new RecordingItem(file.FullName, file.Length, file.LastWriteTimeUtc, header.Kind, null, null);
            }

            using var reader = RecordingReader.Open(file.FullName, key);
            var first = reader.Events().FirstOrDefault();
            var info = first?.Type == RecordingEventType.Info ? RecordingFile.ReadInfo(first.Payload) : null;
            return new RecordingItem(file.FullName, file.Length, file.LastWriteTimeUtc, header.Kind, info, info is null ? "Can't be read" : null);
        }
        catch (RecordingKeyException)
        {
            return new RecordingItem(file.FullName, file.Length, file.LastWriteTimeUtc, null, null, "Made with another key");
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new RecordingItem(file.FullName, file.Length, file.LastWriteTimeUtc, null, null, "Not a recording Orrery can read");
        }
    }

    async Task PlayAsync()
    {
        if (Selected is { Problem: null } item && _privateKey is { } key)
        {
            var player = new PlayerWindow(_service, item, key.ToArray());
            player.Show(this);
            await Task.CompletedTask;
        }
    }

    async Task ExportAsync()
    {
        if (Selected is { Problem: null } item && _privateKey is { } key)
        {
            await RecordingExport.ExportAsync(this, _service, item.Path, key, System.IO.Path.GetFileNameWithoutExtension(item.Path));
        }
    }

    void ShowInFolder()
    {
        if (Selected is not { } item)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add($"/select,{item.Path}");
                Process.Start(start);
            }
            else
            {
                Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(item.Path)!) { UseShellExecute = true });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _status.Text = $"Couldn't open the folder: {e.Message}";
        }
    }

    async Task DeleteAsync()
    {
        if (Selected is not { } item)
        {
            return;
        }

        if (_service.InUse.Contains(item.Path))
        {
            _status.Text = "That recording is being made or played. Stop it first.";
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, "Delete recording", $"Delete \"{item.Title}\" for good? It can't be got back.", "Delete"))
        {
            return;
        }

        try
        {
            File.Delete(item.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Couldn't delete it: {e.Message}";
            return;
        }

        await LoadAsync();
    }
}
