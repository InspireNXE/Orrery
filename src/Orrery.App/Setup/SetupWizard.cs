using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Orrery.App.Controls;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.App.Setup;

/// <summary>A page of first-launch setup, in the order they're shown.</summary>
enum SetupStep
{
    Data,
    Passwords,
    Import,
    Updates,
    Network,
    Done,
}

/// <summary>A portable copy's data question: the folder Orrery runs from, and this user's folders.</summary>
sealed record PortableSetup(string AppDirectory, PlatformFolders Folders);

/// <summary>What setup ends with: the choices, and for a portable copy the data folder it chose and checked.</summary>
sealed record SetupResult(SetupChoices Choices, DataLocation? Location);

/// <summary>
/// First-launch setup, shown once before the main window: a step at a time, each with Orrery's defaults already
/// chosen, ending on a list of what will happen. A step with nothing to choose isn't shown. For a portable copy it starts
/// by asking where the data goes (design doc §2.2), and writes that to the marker file so it isn't asked again.
/// </summary>
sealed class SetupWizard : Window
{
    static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#0E1014")).ToImmutable();

    readonly PortableSetup? _portable;
    readonly string? _keychainName;
    readonly List<SetupStep> _steps = [];
    readonly Dictionary<SetupStep, Control> _pages = [];
    readonly ListBox _stepList = new() { Name = "SetupSteps", Background = Brushes.Transparent, Padding = new Thickness(8, 0), Classes = { "side" } };
    readonly TextBlock _title = Dialogs.Heading(new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
    readonly TextBlock _subtitle = new() { Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _progress = new() { Foreground = Dialogs.MutedBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Notice _message = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _defaults = new() { Name = "SetupDefaults", Content = "Use defaults" };
    readonly Button _back = new() { Name = "SetupBack", Content = "Back", Padding = new Thickness(16, 6) };
    readonly Button _next = new() { Name = "SetupNext", Content = "Next", Padding = new Thickness(18, 6), IsDefault = true, Classes = { "accent" } };

    readonly RadioButton _besideApp = new() { GroupName = "Data", IsChecked = true };
    readonly RadioButton _elsewhere = new() { GroupName = "Data" };
    readonly TextBox _customPath = new() { PlaceholderText = "Choose a folder", FontFamily = Dialogs.MonoFont, FontSize = 12, IsEnabled = false };

    readonly RadioButton _keychain = new() { GroupName = "Passwords", IsChecked = true };
    readonly RadioButton _vault = new() { GroupName = "Passwords" };

    readonly List<(FoundImport File, ToggleSwitch Toggle, bool Found)> _imports = [];
    readonly StackPanel _importRows = new() { Spacing = 12 };

    readonly ToggleSwitch _updateChecks = Switch();
    readonly RadioButton _ask = new() { Content = "Ask me", GroupName = "WhenUpdateFound" };
    readonly RadioButton _download = new() { Content = "Download it", GroupName = "WhenUpdateFound" };
    readonly ToggleSwitch _previews = Switch();

    readonly ToggleSwitch _hostChecks = Switch();
    readonly RadioButton _inTab = new() { Content = "In a tab", GroupName = "WebPages" };
    readonly RadioButton _inBrowser = new() { Content = "In your browser", GroupName = "WebPages" };

    readonly Grid _summary = new() { ColumnDefinitions = new ColumnDefinitions("110,*"), ColumnSpacing = 12, RowSpacing = 12 };

    SetupStep _current;
    bool _busy;
    bool _acceptUnsavedChoice;
    string? _acceptedOpenFolder;

    /// <param name="portable">Set for a portable copy still to choose its data folder; null leaves out the Data step.</param>
    /// <param name="keychainName">The OS keychain's name where it's on offer (installed copies); null leaves out the Passwords step.</param>
    /// <param name="found">Connection files found on this computer, each offered for import and on by default.</param>
    public SetupWizard(PortableSetup? portable, string? keychainName, IReadOnlyList<FoundImport> found)
    {
        _portable = portable;
        _keychainName = keychainName;

        Title = "Set up Orrery";
        Width = 760;
        Height = 520;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = PageBrush;

        if (portable is not null)
        {
            Add(SetupStep.Data, DataPage(portable));
        }

        if (keychainName is not null)
        {
            Add(SetupStep.Passwords, PasswordsPage(keychainName));
        }

        Add(SetupStep.Import, ImportPage(found));
        Add(SetupStep.Updates, UpdatesPage());
        Add(SetupStep.Network, NetworkPage());
        Add(SetupStep.Done, new StackPanel { Spacing = 14, Children = { Card(_summary) } });
        SetOptions(new SetupChoices { Imports = [.. found.Select(f => f.Path)] });

        var pageHost = new Panel();
        pageHost.Children.AddRange(_pages.Values);
        AutomationProperties.SetName(_stepList, "Setup steps");
        _stepList.SelectionChanged += (_, _) =>
        {
            if (_stepList.SelectedItem is ListBoxItem { Tag: SetupStep step } && step != _current)
            {
                GoTo(step);
            }
        };

        _defaults.Click += (_, _) => UseDefaults();
        _back.Click += (_, _) => Back();
        _next.Click += async (_, _) => await NextAsync();
        AutomationProperties.SetHelpText(_defaults, "Skips to the end, with every choice at its default, to see what will happen before you start.");

        // Enter moves on from anywhere but a push button, which Enter presses; the options would otherwise take it for
        // a click. Escape does nothing, so a stray press never loses what's been chosen.
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None
                && (FocusManager?.GetFocusedElement() is not Button focused || focused is ToggleButton))
            {
                e.Handled = true;
                await NextAsync();
            }
        }, RoutingStrategies.Tunnel);

        Content = new DockPanel
        {
            Children =
            {
                Pane.Docked(new Border
                {
                    Background = Dialogs.PanelBrush,
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Width = 148,
                    Padding = new Thickness(0, 20),
                    Child = _stepList,
                }, Dock.Left),
                Pane.Docked(new Border
                {
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(32, 12, 20, 12),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Pane.Docked(new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { _defaults, _back, _next },
                            }, Dock.Right),
                            _progress,
                        },
                    },
                }, Dock.Bottom),
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Thickness(32, 28, 32, 24),
                        Spacing = 6,
                        Children =
                        {
                            _title,
                            _subtitle,
                            new Panel { Margin = new Thickness(0, 14, 0, 0), Children = { pageHost } },
                            new Panel { Margin = new Thickness(0, 8, 0, 0), Children = { _message } },
                        },
                    },
                },
            },
        };

        GoTo(_steps[0], announce: false);
        Opened += (_, _) => _next.Focus();
    }

    /// <summary>Raised by Start once every choice is made and checked. Setup leaves closing itself to the caller.</summary>
    public event Action<SetupResult>? Completed;

    /// <summary>The steps shown, in order.</summary>
    public IReadOnlyList<SetupStep> Steps => _steps;

    public SetupStep Current => _current;

    /// <summary>What the options say now.</summary>
    public SetupChoices Choices => new()
    {
        Passwords = _keychainName is null ? null : _vault.IsChecked == true ? CredentialStoreKind.VaultFile : CredentialStoreKind.OsKeychain,
        Imports = [.. _imports.Where(i => i.Toggle.IsChecked == true).Select(i => i.File.Path)],
        UpdateChecks = _updateChecks.IsChecked == true,
        WhenUpdateFound = _download.IsChecked == true ? UpdateAction.Download : UpdateAction.Ask,
        UpdatePreviews = _previews.IsChecked == true,
        HostStatusChecks = _hostChecks.IsChecked == true,
        WebPagesInOrrery = _inTab.IsChecked == true,
    };

    void Add(SetupStep step, Control page)
    {
        _steps.Add(step);
        page.IsVisible = false;
        _pages[step] = page;

        var (word, icon) = Label(step);
        var item = new ListBoxItem
        {
            Tag = step,
            Padding = new Thickness(14, 0, 8, 0),
            Margin = new Thickness(0, 0, 0, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    StrokeIcon.Create(icon, 16),
                    new TextBlock { Text = word, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

        // The content is a panel, which a screen reader can't read as the item's name.
        AutomationProperties.SetName(item, word);
        _stepList.Items.Add(item);
    }

    static (string Word, string Icon) Label(SetupStep step) => step switch
    {
        SetupStep.Data => ("Data", Icons.Folder),
        SetupStep.Passwords => ("Passwords", Icons.Lock),
        SetupStep.Import => ("Import", Icons.Download),
        SetupStep.Updates => ("Updates", Icons.Refresh),
        SetupStep.Network => ("Network", Icons.Pulse),
        _ => ("Done", Icons.Check),
    };

    (string Title, string? Subtitle) Heading(SetupStep step) => step switch
    {
        SetupStep.Data => ("Where to keep your data", "Connections, settings and saved passwords go in one folder."),
        SetupStep.Passwords => ("Saved passwords", null),
        SetupStep.Import => ("Import connections", _imports.Any(i => i.Found) ? "Found on this computer:" : "Nothing found on this computer."),
        SetupStep.Updates => ("Updates", "Checks GitHub. Sends only your version."),
        SetupStep.Network => ("Network", null),
        _ => ("All set", "Change any of this in Settings."),
    };

    /// <summary>Shows <paramref name="step"/>, and has the screen reader say which it is.</summary>
    internal void GoTo(SetupStep step, bool announce = true)
    {
        _current = step;
        foreach (var (key, page) in _pages)
        {
            page.IsVisible = key == step;
        }

        if (step == SetupStep.Done)
        {
            FillSummary();
        }

        var (title, subtitle) = Heading(step);
        int at = _steps.IndexOf(step) + 1;
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.IsVisible = subtitle is not null;
        _progress.Text = $"{at} of {_steps.Count}";
        _message.IsVisible = false;

        _back.IsEnabled = at > 1;
        _defaults.IsVisible = step != SetupStep.Done;
        _next.Content = step != SetupStep.Done ? "Next" : _acceptUnsavedChoice ? "Start anyway" : "Start";
        _stepList.SelectedItem = _stepList.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, step));

        if (announce)
        {
            ScreenReader.Announce(_next, $"{title}, step {at} of {_steps.Count}", "Orrery.Setup", ScreenReader.MostRecent);
        }
    }

    internal void Back()
    {
        if (_steps.IndexOf(_current) is > 0 and var at)
        {
            GoTo(_steps[at - 1]);
        }
    }

    /// <summary>The next step; on the last, Start. The Data step's folder is checked before moving on.</summary>
    internal async Task NextAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_current == SetupStep.Done)
        {
            await StartAsync();
            return;
        }

        if (_current == SetupStep.Data && !await Busy(async () => await CheckDataFolderAsync() is not null))
        {
            return;
        }

        GoTo(_steps[_steps.IndexOf(_current) + 1]);
    }

    /// <summary>Puts every option back to its default and jumps to the end, where they're listed.</summary>
    internal void UseDefaults()
    {
        _besideApp.IsChecked = true;
        SetOptions(new SetupChoices { Imports = [.. _imports.Where(i => i.Found).Select(i => i.File.Path)] });
        GoTo(SetupStep.Done);
    }

    void SetOptions(SetupChoices choices)
    {
        _keychain.IsChecked = choices.Passwords != CredentialStoreKind.VaultFile;
        _vault.IsChecked = choices.Passwords == CredentialStoreKind.VaultFile;
        foreach (var (file, toggle, _) in _imports)
        {
            toggle.IsChecked = choices.Imports.Contains(file.Path);
        }

        _updateChecks.IsChecked = choices.UpdateChecks;
        _ask.IsChecked = choices.WhenUpdateFound == UpdateAction.Ask;
        _download.IsChecked = choices.WhenUpdateFound == UpdateAction.Download;
        _previews.IsChecked = choices.UpdatePreviews;
        _hostChecks.IsChecked = choices.HostStatusChecks;
        _inTab.IsChecked = choices.WebPagesInOrrery;
        _inBrowser.IsChecked = !choices.WebPagesInOrrery;
    }

    async Task StartAsync()
    {
        DataLocation? location = null;
        if (_portable is not null)
        {
            location = await Busy(CheckDataFolderAsync);
            if (location is null)
            {
                return;
            }

            if (!_acceptUnsavedChoice && !PortableMarker.For(DataChoice, StoredCustomPath()).TrySave(_portable.AppDirectory))
            {
                _acceptUnsavedChoice = true;
                GoTo(SetupStep.Done, announce: false);
                ShowMessage($"Orrery can't save this choice in {_portable.AppDirectory}, so it will ask again next time. Your data will still be saved.",
                    NoticeKind.Warning);
                return;
            }
        }

        Completed?.Invoke(new SetupResult(Choices, location));
    }

    async Task<T> Busy<T>(Func<Task<T>> work)
    {
        _busy = true;
        _next.IsEnabled = false;
        try
        {
            return await work();
        }
        finally
        {
            _busy = false;
            _next.IsEnabled = true;
        }
    }

    // ---- Data (portable copies) ----

    PortableDataChoice DataChoice => _elsewhere.IsChecked == true ? PortableDataChoice.Custom : PortableDataChoice.WithApp;

    /// <summary>The custom folder as the marker keeps it; null when none is chosen.</summary>
    string? StoredCustomPath() =>
        DataChoice == PortableDataChoice.Custom && !string.IsNullOrWhiteSpace(_customPath.Text)
            ? DataLocationResolver.ToStoredCustomPath(_portable!.AppDirectory, _customPath.Text.Trim())
            : null;

    Control DataPage(PortableSetup portable)
    {
        AutomationProperties.SetName(_customPath, "Folder");
        _elsewhere.IsCheckedChanged += (_, _) => _customPath.IsEnabled = _elsewhere.IsChecked == true;

        var choose = new Button { Content = "Choose…" };
        AutomationProperties.SetName(choose, "Choose a folder");
        choose.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a data folder" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            {
                _customPath.Text = path;
                _elsewhere.IsChecked = true;
            }
        };

        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        customRow.Children.Add(_customPath);
        Grid.SetColumn(choose, 1);
        customRow.Children.Add(choose);

        var besideApp = DataLocationResolver.ForPortable(portable.AppDirectory, portable.Folders, PortableDataChoice.WithApp).DataDir;
        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Option(_besideApp, "Beside Orrery", besideApp, mono: true),
                Option(_elsewhere, "Somewhere else…", "A synced or network folder, say."),
                customRow,
            },
        });
    }

    /// <summary>
    /// The chosen folder's place, once it's checked: Orrery can write there, and if other people could change it the
    /// user has said to go ahead. Null when not, with the reason shown.
    /// </summary>
    async Task<DataLocation?> CheckDataFolderAsync()
    {
        var portable = _portable!;
        var choice = DataChoice;
        string? storedCustomPath = null;
        if (choice == PortableDataChoice.Custom)
        {
            if (string.IsNullOrWhiteSpace(_customPath.Text))
            {
                return Refuse("Choose a folder first.");
            }

            try
            {
                storedCustomPath = StoredCustomPath();
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
            {
                return Refuse($"That isn't a folder Orrery can use: {e.Message}");
            }
        }

        var location = DataLocationResolver.ForPortable(portable.AppDirectory, portable.Folders, choice, storedCustomPath);
        if (!DataLocationResolver.IsWritable(location.DataDir))
        {
            return Refuse($"Orrery can't write to {location.DataDir}. It may be read-only, or protected by the OS. Choose another folder.");
        }

        if (location.DataDir != _acceptedOpenFolder && OpenFolderWarning(choice, location.DataDir) is { } warning)
        {
            if (!await Dialogs.ConfirmAsync(this, "Other people can change this folder", warning, "Use anyway"))
            {
                if (_current != SetupStep.Data)
                {
                    GoTo(SetupStep.Data);
                }

                return null;
            }

            _acceptedOpenFolder = location.DataDir;
        }

        return location;
    }

    DataLocation? Refuse(string reason)
    {
        if (_current != SetupStep.Data)
        {
            GoTo(SetupStep.Data);
        }

        ShowMessage(reason, NoticeKind.Error);
        return null;
    }

    /// <summary>
    /// Null unless other users of this computer could change the chosen folder. Beside Orrery, they could then also
    /// replace Orrery itself, which no ACL on the data folder prevents.
    /// </summary>
    string? OpenFolderWarning(PortableDataChoice choice, string dataDir)
    {
        var appDirectory = _portable!.AppDirectory;
        if (choice == PortableDataChoice.WithApp && FolderAccess.IsOpenToOthers(appDirectory))
        {
            return $"{FolderAccess.OpenToOthersWarning} They could also replace Orrery itself.\n\n{appDirectory}";
        }

        return FolderAccess.IsOpenToOthers(dataDir)
            ? $"{FolderAccess.OpenToOthersWarning}\n\n{dataDir}"
            : null;
    }

    void ShowMessage(string text, NoticeKind kind)
    {
        _message.Text = text;
        _message.Kind = kind;
        _message.IsVisible = true;
    }

    // ---- Passwords (installed copies with a keychain) ----

    Control PasswordsPage(string keychainName) => Card(new StackPanel
    {
        Spacing = 12,
        Children =
        {
            Option(_keychain, KeychainTitle(keychainName), "Unlocks when you sign in"),
            Option(_vault, "Encrypted vault", "Master password, travels with your data"),
        },
    });

    /// <summary>"Windows Credential Manager", "macOS Keychain": the backend's name without its "the".</summary>
    static string KeychainTitle(string name)
    {
        var bare = name.StartsWith("the ", StringComparison.Ordinal) ? name[4..] : name;
        return bare.Length == 0 ? bare : char.ToUpperInvariant(bare[0]) + bare[1..];
    }

    // ---- Import ----

    Control ImportPage(IReadOnlyList<FoundImport> found)
    {
        foreach (var file in found)
        {
            AddImportRow(file, isFound: true);
        }

        var another = new Button { Content = "Another file…", HorizontalAlignment = HorizontalAlignment.Left };
        another.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import connections",
                AllowMultiple = false,
                FileTypeFilter = MainWindow.ImportFileTypes,
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                AddFile(path);
            }
        };

        var page = new StackPanel { Spacing = 14 };
        if (found.Count > 0)
        {
            page.Children.Add(Card(_importRows));
        }
        else
        {
            _importRows.IsVisible = false;
            page.Children.Add(_importRows);
        }

        page.Children.Add(another);
        page.Children.Add(Note("You'll see a preview first."));
        return page;
    }

    /// <summary>Adds a file chosen with Another file, set to import; one already listed is just switched on.</summary>
    internal void AddFile(string path)
    {
        if (!MainWindow.CanImport(path))
        {
            ShowMessage("Orrery can't import this kind of file.", NoticeKind.Error);
            return;
        }

        _message.IsVisible = false;
        if (_imports.FirstOrDefault(i => string.Equals(i.File.Path, path, StringComparison.OrdinalIgnoreCase)) is { Toggle: { } listed })
        {
            listed.IsChecked = true;
            return;
        }

        AddImportRow(new FoundImport(path, ImportSources.KindOf(path)), isFound: false);
        if (_importRows.Parent is StackPanel page && !_importRows.IsVisible)
        {
            // Nothing was found, so the list had no card yet.
            page.Children.Remove(_importRows);
            page.Children.Insert(0, Card(_importRows));
            _importRows.IsVisible = true;
        }
    }

    void AddImportRow(FoundImport file, bool isFound)
    {
        var toggle = new ToggleSwitch { IsChecked = true, OnContent = "Import", OffContent = "Skip", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, $"Import {file.Kind}");
        AutomationProperties.SetHelpText(toggle, file.Path);
        _imports.Add((file, toggle, isFound));
        _importRows.Children.Add(new DockPanel
        {
            Children =
            {
                Pane.Docked(toggle, Dock.Right),
                Described(file.Kind, file.Path, mono: true),
            },
        });
    }

    // ---- Updates ----

    Control UpdatesPage()
    {
        AutomationProperties.SetName(_updateChecks, "Check automatically");
        AutomationProperties.SetHelpText(_updateChecks, "Daily");
        AutomationProperties.SetName(_previews, "Previews");
        AutomationProperties.SetHelpText(_previews, "Offer preview releases too.");
        return Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SwitchRow(_updateChecks, "Check automatically", "Daily"),
                ChoiceRow("When one is out", _ask, _download),
                SwitchRow(_previews, "Previews", "Offer preview releases too."),
            },
        });
    }

    // ---- Network ----

    Control NetworkPage()
    {
        AutomationProperties.SetName(_hostChecks, "Show which hosts are up");
        AutomationProperties.SetHelpText(_hostChecks, "Quick TCP check to your own hosts");
        return Card(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                SwitchRow(_hostChecks, "Show which hosts are up", "Quick TCP check to your own hosts"),
                ChoiceRow("Open web pages", _inTab, _inBrowser),
            },
        });
    }

    // ---- Done ----

    /// <summary>One row per choice, saying what will happen.</summary>
    internal IReadOnlyList<(string Label, string Value)> Summary()
    {
        var choices = Choices;
        var rows = new List<(string, string)>();
        if (_portable is not null)
        {
            rows.Add(("Data", DataChoice == PortableDataChoice.WithApp
                ? $"Beside Orrery, in {DataLocationResolver.ForPortable(_portable.AppDirectory, _portable.Folders, PortableDataChoice.WithApp).DataDir}"
                : string.IsNullOrWhiteSpace(_customPath.Text) ? "Somewhere else, still to choose" : _customPath.Text.Trim()));
        }

        rows.Add(("Passwords", choices.Passwords == CredentialStoreKind.OsKeychain ? KeychainTitle(_keychainName!) : "Encrypted vault"));
        var imports = _imports.Where(i => i.Toggle.IsChecked == true).Select(i => i.File.Kind).ToList();
        rows.Add(("Import", imports.Count > 0 ? string.Join(", ", imports) : "Nothing"));
        rows.Add(("Updates", !choices.UpdateChecks ? "Not checked automatically"
            : choices.WhenUpdateFound == UpdateAction.Ask ? "Checked daily; ask me" : "Checked daily; download it"));
        rows.Add(("Previews", choices.UpdatePreviews ? "On" : "Off"));
        rows.Add(("Host status", choices.HostStatusChecks ? "Shown" : "Not checked"));
        rows.Add(("Web pages", choices.WebPagesInOrrery ? "In a tab" : "In your browser"));
        return rows;
    }

    void FillSummary()
    {
        _summary.Children.Clear();
        _summary.RowDefinitions.Clear();
        foreach (var ((label, value), row) in Summary().Select((r, i) => (r, i)))
        {
            _summary.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = label, Foreground = Dialogs.MutedBrush };
            var text = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(text, $"{label}: {value}");
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            _summary.Children.Add(name);
            _summary.Children.Add(text);
        }
    }

    // ---- Layout helpers ----

    static ToggleSwitch Switch() => new()
    {
        OnContent = null,
        OffContent = null,
        MinWidth = 0,
        Margin = new Thickness(10, 0, -11, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Control Option(RadioButton radio, string title, string line, bool mono = false)
    {
        radio.Content = Described(title, line, mono);

        // The content is a panel, which a screen reader can't read as the button's name.
        AutomationProperties.SetName(radio, title);
        AutomationProperties.SetHelpText(radio, line);
        return radio;
    }

    /// <summary>A name over a muted line; a path's line is in the mono font.</summary>
    static Control Described(string title, string line, bool mono = false)
    {
        var detail = new TextBlock { Text = line, Foreground = Dialogs.MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        if (mono)
        {
            detail.FontFamily = Dialogs.MonoFont;
            detail.FontSize = 11;
        }

        return new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, detail },
        };
    }

    static Control SwitchRow(ToggleSwitch toggle, string label, string note) => new DockPanel
    {
        Children =
        {
            Pane.Docked(toggle, Dock.Right),
            Described(label, note),
        },
    };

    static Control ChoiceRow(string label, params RadioButton[] choices)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.AddRange(choices);
        AutomationProperties.SetName(buttons, label);
        return new DockPanel
        {
            Children =
            {
                Pane.Docked(buttons, Dock.Right),
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    static Border Card(Control content) => new()
    {
        Background = Dialogs.PanelBrush,
        BorderBrush = Pane.LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(20, 16),
        Child = content,
    };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
}
