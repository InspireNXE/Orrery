using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.Core.Sessions;
using Orrery.Protocols.Ssh;

namespace Orrery.App.Sessions;

/// <summary>
/// Files on an SSH server, in a session tab beside its terminal: browse folders, upload (button or drop files
/// in), download, make folders, rename and delete. One transfer runs at a time, with progress and Cancel;
/// browsing goes on meanwhile. Keys: Enter opens, Backspace goes up, F2 renames, Delete deletes, F5 refreshes,
/// Ctrl+Shift+C copies paths, Ctrl+Shift+N makes a folder (Cmd on macOS). Right-click opens a menu of the same for
/// what's selected, or for the folder on empty space (<see cref="MenuFor"/>).
/// </summary>
sealed class FileBrowserView : DockPanel
{
    static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#E0B45A")).ToImmutable();
    const string Columns = "28,*,96,150,104";

    readonly SftpSession _session;
    readonly TextBox _path = new() { FontFamily = Dialogs.MonoFont, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ListBox _list = new() { SelectionMode = SelectionMode.Multiple, Background = Brushes.Transparent };
    readonly Notice _status = StatusLine();
    readonly ProgressBar _progress = new() { Width = 160, Height = 6, MinHeight = 6, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(10, 2), MinHeight = 0, IsVisible = false };
    readonly Notice _message = new(NoticeKind.Info) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    readonly Control _browser;
    readonly Button _download, _rename, _delete;
    string _current = "/";
    CancellationTokenSource? _transfer;
    int _listing;

    public FileBrowserView(SftpSession session)
    {
        _session = session;
        Focusable = true;

        var up = ToolButton(Icons.Up, "Up a folder (Backspace)", () => _ = OpenAsync(SftpSession.Parent(_current)));
        var refresh = ToolButton(Icons.Refresh, "Refresh (F5)", () => _ = OpenAsync(_current));
        var newFolder = ToolButton(Icons.FolderPlus, $"New folder ({(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Shift+N)", () => _ = NewFolderAsync());
        var upload = ToolButton(Icons.Upload, "Upload", null);
        upload.Flyout = new MenuFlyout
        {
            Items =
            {
                MenuItem("Files…", () => _ = PickAndUploadAsync(folders: false)),
                MenuItem("Folder…", () => _ = PickAndUploadAsync(folders: true)),
            },
        };
        _download = ToolButton(Icons.Download, "Download (Enter on a file)", () => _ = DownloadSelectedAsync());
        _rename = ToolButton(Icons.Rename, "Rename (F2)", () => _ = RenameAsync());
        _delete = ToolButton(Icons.Trash, "Delete (Delete)", () => _ = DeleteSelectedAsync());

        _path.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_path.Text))
            {
                e.Handled = true;
                _ = OpenAsync(_path.Text.Trim());
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _path.Text = _current;
                _list.Focus();
            }
        };

        var toolbar = new DockPanel
        {
            Margin = new Thickness(12, 10, 12, 8),
            Children =
            {
                Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 8, 0), Children = { up, refresh } }, Dock.Left),
                Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0, 0, 0), Children = { newFolder, upload, _download, _rename, _delete } }, Dock.Right),
                _path,
            },
        };

        var header = Row(new TextBlock(), Header("Name"), Header("Size", right: true), Header("Modified"), Header("Permissions"));
        header.Margin = new Thickness(12, 0, 12, 4);

        _list.ItemTemplate = new FuncDataTemplate<SftpItem>((item, _) => item is null ? new Panel() : Row(
            new StrokeIcon
            {
                Data = Geometry.Parse(item.IsDirectory ? Icons.Folder : Icons.File),
                Width = 15,
                Height = 15,
                Foreground = item.IsDirectory ? FolderBrush : Dialogs.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
            new TextBlock { Text = item.IsLink ? $"{item.Name}  →" : item.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            Cell(item.IsDirectory ? string.Empty : FormatSize(item.Size), right: true),
            Cell(item.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)),
            Cell(item.Permissions, mono: true)));
        // A row is a grid of cells, so each one is named in words: "notes.txt, file, 2.0 KB, modified …".
        _list.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is SftpItem item)
            {
                AutomationProperties.SetName(e.Container, Spoken(item));
            }
        };
        AutomationProperties.SetName(_list, "Files");
        AutomationProperties.SetHelpText(_list, "Enter opens, Backspace goes up a folder, F2 renames, Delete deletes, F5 refreshes, "
            + $"{(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Shift+C copies the path, {(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Shift+N makes a folder. "
            + "The menu key or Shift+F10 opens the menu.");
        AutomationProperties.SetName(_path, "Folder path");
        AutomationProperties.SetHelpText(_path, "Type a folder and press Enter to open it.");
        AutomationProperties.SetName(_progress, "Transfer progress");
        AutomationProperties.SetLiveSetting(_status, ScreenReader.LiveRegionSetting);
        // The status line is a Notice, so its text is in its Message; an error is read with "Error:" before it.
        _status.Message.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && e.NewValue is string { Length: > 0 } text)
            {
                ScreenReader.Announce(this, _status.Prefix is { } prefix ? $"{prefix}: {text}" : text, "Orrery.Files", ScreenReader.MostRecent);
            }
        };
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>() is { DataContext: SftpItem item })
            {
                _ = OpenItemAsync(item);
            }
        };
        _list.KeyDown += OnListKeyDown;
        // Right-click on a row selects it (the list does that); on empty space it clears the selection, so the menu
        // is the folder's own. The menu is built as it opens, from what's selected then.
        _list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_list).Properties.IsRightButtonPressed
                && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null)
            {
                _list.SelectedItems?.Clear();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var entry in MenuFor(Selected(), _transfer is not null))
            {
                menu.Items.Add(entry is null ? new Separator() : MenuItemFor(entry));
            }
        };
        _list.ContextMenu = menu;
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && _transfer is null ? DragDropEffects.Copy : DragDropEffects.None);
        _list.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths is { Count: > 0 })
            {
                _ = UploadAsync(paths);
            }
        });

        _cancel.Click += (_, _) => _transfer?.Cancel();
        var statusBar = new DockPanel
        {
            Margin = new Thickness(12, 6),
            Children =
            {
                Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _progress, _cancel } }, Dock.Right),
                _status,
            },
        };

        _browser = new DockPanel
        {
            IsVisible = false,
            Children =
            {
                Pane.Docked(toolbar, Dock.Top),
                Pane.Docked(header, Dock.Top),
                Pane.Docked(statusBar, Dock.Bottom),
                _list,
            },
        };
        _message.Message.TextAlignment = TextAlignment.Center;
        Children.Add(new Panel { Children = { _browser, _message } });

        session.StateChanged += (_, e) => Dispatcher.UIThread.Post(() => ShowState(e.State, e.Message));
        ShowState(session.State, null);
        UpdateButtons();
    }

    public SftpSession Session => _session;

    /// <summary>The folder being shown.</summary>
    public string CurrentFolder => _current;

    /// <summary>What's listed, for tests.</summary>
    internal IReadOnlyList<SftpItem> Items => _list.ItemsSource as IReadOnlyList<SftpItem> ?? [];

    void ShowState(SessionState state, string? message)
    {
        bool connected = state == SessionState.Connected;
        _browser.IsVisible = connected || (state != SessionState.Connecting && _list.ItemsSource is not null);
        _browser.IsEnabled = connected;
        _message.IsVisible = !connected;
        _message.Kind = state == SessionState.Failed ? NoticeKind.Error : NoticeKind.Info;
        _message.Text = state switch
        {
            SessionState.Connecting => $"Connecting to {_session.Target.Host}…",
            SessionState.Failed => $"Connection failed: {message}",
            SessionState.Disconnected => "Disconnected.",
            _ => null,
        };
        if (_browser.IsVisible && !connected)
        {
            // Keep the last listing in view, dimmed, with the reason over it.
            _browser.Opacity = 0.35;
        }
        else
        {
            _browser.Opacity = 1;
        }

        if (connected && _list.ItemsSource is null)
        {
            _ = OpenAsync(_session.HomeDirectory);
        }
    }

    /// <summary>Lists <paramref name="folder"/>; an error leaves the current listing and says why.</summary>
    public async Task OpenAsync(string folder)
    {
        int listing = ++_listing;
        _status.Text = "Loading…";
        try
        {
            var items = await _session.ListAsync(folder);
            if (listing != _listing)
            {
                return;
            }

            _current = folder;
            _path.Text = folder;
            _list.ItemsSource = items;
            ShowCount();
        }
        catch (SftpException e)
        {
            if (listing == _listing)
            {
                _path.Text = _current;
                ShowError($"Couldn't open {folder}: {e.Message}");
            }
        }

        UpdateButtons();
    }

    Task OpenItemAsync(SftpItem item) => item.IsDirectory ? OpenAsync(item.FullPath) : DownloadAsync([item]);

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        var selected = Selected();
        var command = (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift;
        switch (e.Key)
        {
            case Key.C when e.KeyModifiers == command:
                _ = CopyAsync(selected.Count > 0 ? string.Join(Environment.NewLine, selected.Select(i => i.FullPath)) : _current);
                break;
            case Key.N when e.KeyModifiers == command:
                _ = NewFolderAsync();
                break;
            case Key.Enter when selected.Count == 1:
                _ = OpenItemAsync(selected[0]);
                break;
            case Key.Enter when selected.Count > 1:
                _ = DownloadAsync(selected);
                break;
            case Key.Back:
                _ = OpenAsync(SftpSession.Parent(_current));
                break;
            case Key.F2 when selected.Count == 1:
                _ = RenameAsync();
                break;
            case Key.Delete when selected.Count > 0:
                _ = DeleteSelectedAsync();
                break;
            case Key.F5:
                _ = OpenAsync(_current);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    List<SftpItem> Selected() => _list.SelectedItems?.OfType<SftpItem>().ToList() ?? [];

    // ---- The right-click menu ----

    /// <summary>What a right-click menu item does.</summary>
    internal enum FileAction { Open, Download, Rename, Delete, CopyPath, CopyName, UploadFiles, UploadFolder, NewFolder, Refresh }

    /// <summary>One item in the right-click menu, with the key that does the same shown beside it.</summary>
    internal sealed record FileMenuEntry(string Header, FileAction Action, string? Shortcut = null, bool Enabled = true);

    /// <summary>
    /// The right-click menu for what's selected; null entries are separators. One file: download, rename, delete and
    /// copy its path or name; a folder opens first; several: download, delete or copy their paths; nothing (empty
    /// space): upload, new folder, refresh and copy the folder's path. A transfer under way leaves downloads and
    /// uploads off, as the toolbar does.
    /// </summary>
    internal static IReadOnlyList<FileMenuEntry?> MenuFor(IReadOnlyList<SftpItem> selected, bool busy)
    {
        var command = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        return selected switch
        {
            [] =>
            [
                new("Upload files…", FileAction.UploadFiles, Enabled: !busy),
                new("Upload a folder…", FileAction.UploadFolder, Enabled: !busy),
                new("New folder…", FileAction.NewFolder, $"{command}+Shift+N"),
                null,
                new("Refresh", FileAction.Refresh, "F5"),
                new("Copy this folder's path", FileAction.CopyPath, $"{command}+Shift+C"),
            ],
            [var one] =>
            [
                .. one.IsDirectory
                    ? (FileMenuEntry[])[new("Open", FileAction.Open), new("Download…", FileAction.Download, Enabled: !busy)]
                    : [new("Download…", FileAction.Download, Enabled: !busy)],
                null,
                new("Rename…", FileAction.Rename, "F2"),
                new("Delete…", FileAction.Delete, "Delete"),
                null,
                new("Copy path", FileAction.CopyPath, $"{command}+Shift+C"),
                new("Copy name", FileAction.CopyName),
            ],
            _ =>
            [
                new($"Download {selected.Count} items…", FileAction.Download, Enabled: !busy),
                null,
                new("Rename…", FileAction.Rename, "F2", Enabled: false),
                new($"Delete {selected.Count} items…", FileAction.Delete, "Delete"),
                null,
                new("Copy paths", FileAction.CopyPath, $"{command}+Shift+C"),
            ],
        };
    }

    MenuItem MenuItemFor(FileMenuEntry entry)
    {
        var item = new MenuItem { Header = entry.Header, IsEnabled = entry.Enabled };
        if (entry.Shortcut is { } shortcut)
        {
            item.InputGesture = KeyGesture.Parse(shortcut.Replace("Cmd", "Meta"));
        }

        item.Click += (_, _) => Do(entry.Action);
        return item;
    }

    void Do(FileAction action)
    {
        var selected = Selected();
        _ = action switch
        {
            FileAction.Open when selected is [var one] => OpenItemAsync(one),
            FileAction.Download => DownloadAsync(selected),
            FileAction.Rename => RenameAsync(),
            FileAction.Delete => DeleteSelectedAsync(),
            FileAction.CopyPath => CopyAsync(selected.Count > 0 ? string.Join(Environment.NewLine, selected.Select(i => i.FullPath)) : _current),
            FileAction.CopyName when selected is [var one] => CopyAsync(one.Name),
            FileAction.UploadFiles => PickAndUploadAsync(folders: false),
            FileAction.UploadFolder => PickAndUploadAsync(folders: true),
            FileAction.NewFolder => NewFolderAsync(),
            FileAction.Refresh => OpenAsync(_current),
            _ => Task.CompletedTask,
        };
    }

    async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            _status.Kind = NoticeKind.Info;
            _status.Text = text.Contains('\n') ? "Copied the paths." : $"Copied {text}";
        }
    }

    void UpdateButtons()
    {
        int count = Selected().Count;
        bool idle = _transfer is null;
        _download.IsEnabled = count > 0 && idle;
        _rename.IsEnabled = count == 1;
        _delete.IsEnabled = count > 0;
    }

    // ---- Changing things ----

    async Task NewFolderAsync()
    {
        if (Owner is not { } owner || await Dialogs.PromptTextAsync(owner, "New folder", $"In {_current}", "New folder", "Create") is not { } name)
        {
            return;
        }

        await DoAsync(() => _session.CreateFolderAsync(SftpSession.Combine(_current, name)), $"Couldn't create {name}");
    }

    async Task RenameAsync()
    {
        if (Selected() is not [var item] || Owner is not { } owner
            || await Dialogs.PromptTextAsync(owner, $"Rename {item.Name}", "New name", item.Name, "Rename") is not { } name || name == item.Name)
        {
            return;
        }

        await DoAsync(() => _session.RenameAsync(item.FullPath, SftpSession.Combine(_current, name)), $"Couldn't rename {item.Name}");
    }

    async Task DeleteSelectedAsync()
    {
        var items = Selected();
        if (items.Count == 0 || Owner is not { } owner)
        {
            return;
        }

        bool folders = items.Any(i => i.IsDirectory && !i.IsLink);
        string what = items.Count == 1 ? $"\"{items[0].Name}\"" : $"{items.Count} items";
        if (!await Dialogs.ConfirmAsync(owner, $"Delete {what}?",
                $"This deletes {what} on {_session.Target.Host}{(folders ? ", with everything in the folders" : string.Empty)}. It can't be undone.", "Delete"))
        {
            return;
        }

        await DoAsync(async () =>
        {
            foreach (var item in items)
            {
                _status.Text = $"Deleting {item.Name}…";
                await _session.DeleteAsync(item);
            }
        }, "Couldn't delete everything");
    }

    /// <summary>Runs a change, then lists the folder again whether it worked or not.</summary>
    async Task DoAsync(Func<Task> change, string failure)
    {
        string? error = null;
        try
        {
            await change();
        }
        catch (SftpException e)
        {
            error = $"{failure}: {e.Message}";
        }

        await OpenAsync(_current);
        if (error is not null)
        {
            ShowError(error);
        }
    }

    // ---- Transfers ----

    async Task PickAndUploadAsync(bool folders)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        IEnumerable<IStorageItem> picked = folders
            ? await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Upload folders to {_current}", AllowMultiple = true })
            : await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = $"Upload files to {_current}", AllowMultiple = true });
        var paths = picked.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            await UploadAsync(paths);
        }
    }

    /// <summary>Uploads local files and folders into the current folder, asking first if that replaces anything.</summary>
    public async Task UploadAsync(IReadOnlyList<string> paths)
    {
        if (_transfer is not null)
        {
            return;
        }

        var existing = Items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var clashes = paths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).Where(existing.Contains).ToList();
        if (clashes.Count > 0 && Owner is { } owner
            && !await Dialogs.ConfirmAsync(owner, "Replace existing items?", $"{Describe(clashes)} already in {_current}. Uploading replaces files with the same name.", "Replace"))
        {
            return;
        }

        string folder = _current;
        await TransferAsync($"Uploading {Describe(paths.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToList())}",
            _ => Task.FromResult(SftpSession.LocalSize(paths)),
            (progress, token) => _session.UploadAsync(paths, folder, progress, token));
        await OpenAsync(_current);
    }

    async Task DownloadSelectedAsync() => await DownloadAsync(Selected());

    /// <summary>Downloads into a folder this computer picks: a save dialog for one file, a folder picker otherwise.</summary>
    async Task DownloadAsync(IReadOnlyList<SftpItem> items)
    {
        if (items.Count == 0 || _transfer is not null || Owner is not { } owner)
        {
            return;
        }

        string? folder;
        var files = items;
        if (items is [{ IsDirectory: false } single])
        {
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = $"Download {single.Name}", SuggestedFileName = SftpSession.LocalName(single.Name) });
            if (file?.TryGetLocalPath() is not { } local)
            {
                return;
            }

            // Saved under the chosen name, which may differ from the server's.
            folder = Path.GetDirectoryName(local)!;
            files = [single with { Name = Path.GetFileName(local) }];
        }
        else
        {
            var picked = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Download {items.Count} items to" });
            folder = picked.FirstOrDefault()?.TryGetLocalPath();
            if (folder is null)
            {
                return;
            }

            var clashes = items.Select(i => SftpSession.LocalName(i.Name)).Where(n => Path.Exists(Path.Combine(folder, n))).ToList();
            if (clashes.Count > 0 && !await Dialogs.ConfirmAsync(owner, "Replace existing items?", $"{Describe(clashes)} already in {folder}. Downloading replaces files with the same name.", "Replace"))
            {
                return;
            }
        }

        await TransferAsync($"Downloading {Describe(items.Select(i => i.Name).ToList())}",
            token => _session.RemoteSizeAsync(items, token),
            (progress, token) => _session.DownloadAsync(files, folder, progress, token));
    }

    /// <summary>Runs one transfer with a progress bar and Cancel, and says how it ended.</summary>
    async Task TransferAsync(string what, Func<CancellationToken, Task<long>> size, Func<IProgress<long>, CancellationToken, Task> transfer)
    {
        using var cts = new CancellationTokenSource();
        _transfer = cts;
        _progress.IsVisible = _cancel.IsVisible = true;
        _progress.IsIndeterminate = true;
        _status.Kind = NoticeKind.Info;
        _status.Text = $"{what}…";
        UpdateButtons();
        try
        {
            long total = await size(cts.Token);
            _progress.IsIndeterminate = total == 0;
            _progress.Maximum = Math.Max(total, 1);
            _progress.Value = 0;
            var progress = new Progress<long>(done =>
            {
                _progress.Value = done;
                _status.Text = total > 0 ? $"{what}: {FormatSize(done)} of {FormatSize(total)}" : $"{what}…";
            });

            await transfer(progress, cts.Token);
            _status.Text = $"{what}: done.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"{what}: canceled.";
        }
        catch (SftpException e)
        {
            ShowError($"{what}: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError($"{what}: {e.Message}");
        }
        finally
        {
            _transfer = null;
            _progress.IsVisible = _cancel.IsVisible = false;
            UpdateButtons();
        }
    }

    // ---- Helpers ----

    Window? Owner => TopLevel.GetTopLevel(this) as Window;

    void ShowCount()
    {
        int folders = Items.Count(i => i.IsDirectory), files = Items.Count - folders;
        _status.Kind = NoticeKind.Info;
        _status.Text = $"{folders} {(folders == 1 ? "folder" : "folders")}, {files} {(files == 1 ? "file" : "files")}";
    }

    void ShowError(string text)
    {
        _status.Kind = NoticeKind.Error;
        _status.Text = text;
        ToolTip.SetTip(_status, text);
    }

    /// <summary>A row as a screen reader reads it.</summary>
    internal static string Spoken(SftpItem item)
    {
        var kind = item.IsLink ? (item.IsDirectory ? "folder link" : "link") : item.IsDirectory ? "folder" : "file";
        var size = item.IsDirectory ? string.Empty : $", {FormatSize(item.Size)}";
        return $"{item.Name}, {kind}{size}, modified {item.Modified.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)}";
    }

    /// <summary>The one-line status under the list: counts and progress, or an error with its icon.</summary>
    static Notice StatusLine()
    {
        var line = new Notice(NoticeKind.Info) { VerticalAlignment = VerticalAlignment.Center, IconSize = 13 };
        line.Message.FontSize = 12;
        line.Message.TextWrapping = TextWrapping.NoWrap;
        line.Message.TextTrimming = TextTrimming.CharacterEllipsis;
        return line;
    }

    static string Describe(IReadOnlyList<string> names) => names.Count == 1 ? $"\"{names[0]}\"" : $"{names.Count} items";

    /// <summary>Bytes as B, KB, MB, GB or TB, in powers of 1024.</summary>
    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size.ToString(size < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    static Grid Row(Control icon, Control name, Control size, Control modified, Control permissions)
    {
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse(Columns), Margin = new Thickness(0, 2) };
        foreach (var (cell, column) in new[] { (icon, 0), (name, 1), (size, 2), (modified, 3), (permissions, 4) })
        {
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }

        return grid;
    }

    static TextBlock Header(string text, bool right = false) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Dialogs.MutedBrush,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        Margin = right ? new Thickness(0, 0, 16, 0) : default,
    };

    static TextBlock Cell(string text, bool right = false, bool mono = false) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Dialogs.MutedBrush,
        FontFamily = mono ? Dialogs.MonoFont : FontFamily.Default,
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        Margin = right ? new Thickness(0, 0, 16, 0) : default,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    static Button ToolButton(string icon, string tip, Action? click)
    {
        var button = new Button { Content = StrokeIcon.Create(icon, 16), Width = 30, Height = 30 };
        button.Classes.Add("icon");
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        if (click is not null)
        {
            button.Click += (_, _) => click();
        }

        return button;
    }

    static MenuItem MenuItem(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }
}
