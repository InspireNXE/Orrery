using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Orrery.App.Controls;
using Orrery.Core.Storage;

namespace Orrery.App.Storage;

/// <summary>
/// First launch of a portable copy: choose where its data lives (design doc §2.2). The choice is
/// written back to the marker file so it isn't asked again.
/// </summary>
sealed class PortableSetupWindow : Window
{

    readonly string _appDirectory;
    readonly PlatformFolders _folders;
    readonly RadioButton _withApp, _userProfile, _custom;
    readonly TextBox _customPath;
    readonly Notice _message;
    readonly Button _continue;
    bool _acceptUnsavedChoice;
    string? _acceptedOpenFolder;

    public PortableSetupWindow(string appDirectory, PlatformFolders folders)
    {
        _appDirectory = appDirectory;
        _folders = folders;

        Title = "Set up portable Orrery";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Dialogs.PanelBrush;

        _withApp = new RadioButton { GroupName = "location", IsChecked = true };
        _userProfile = new RadioButton { GroupName = "location" };
        _custom = new RadioButton { GroupName = "location" };
        _customPath = new TextBox { Watermark = "Choose a folder", FontFamily = Dialogs.MonoFont, FontSize = 12, IsEnabled = false };
        Avalonia.Automation.AutomationProperties.SetName(_customPath, "Custom folder");
        _custom.IsCheckedChanged += (_, _) => _customPath.IsEnabled = _custom.IsChecked == true;

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await BrowseAsync();

        _message = new Notice(NoticeKind.Error) { IsVisible = false };
        _continue = new Button { Content = "Continue", Padding = new Thickness(18, 6), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        _continue.Classes.Add("accent");
        _continue.Click += async (_, _) => await TryContinueAsync();

        var customRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(28, 0, 0, 0) };
        customRow.Children.Add(_customPath);
        Grid.SetColumn(browse, 1);
        customRow.Children.Add(browse);

        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(24),
            Children =
            {
                new TextBlock { Text = "Where should this copy keep its data?", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Connections, settings, trusted certificates and the password vault all go in one folder. "
                         + "You can move it later in Settings.",
                    TextWrapping = TextWrapping.Wrap,
                },
                Option(_withApp, "With the app", "Carry everything on a USB stick.", DataLocationResolver.ForPortable(appDirectory, folders, PortableDataChoice.WithApp).DataDir),
                Option(_userProfile, "This computer's user profile", "The app runs from anywhere; data stays on this machine.", DataLocationResolver.ForPortable(appDirectory, folders, PortableDataChoice.UserProfile).DataDir),
                Option(_custom, "Custom folder", "For example a synced or network drive.", null),
                customRow,
                _message,
                _continue,
            },
        };
    }

    /// <summary>Raised once a location is chosen and checked.</summary>
    public event Action<DataLocation>? Completed;

    static Control Option(RadioButton radio, string title, string description, string? path)
    {
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        text.Children.Add(new TextBlock { Text = description, Foreground = Dialogs.MutedBrush, FontSize = 12 });
        if (path is not null)
        {
            text.Children.Add(new TextBlock { Text = path, Foreground = Dialogs.MutedBrush, FontSize = 11, FontFamily = Dialogs.MonoFont, TextWrapping = TextWrapping.Wrap });
        }

        radio.Content = text;

        // The content is a panel, which a screen reader can't read as the button's name.
        Avalonia.Automation.AutomationProperties.SetName(radio, title);
        Avalonia.Automation.AutomationProperties.SetHelpText(radio, path is null ? description : $"{description} {path}");
        return radio;
    }

    async Task BrowseAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a data folder" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _customPath.Text = path;
            _custom.IsChecked = true;
        }
    }

    async Task TryContinueAsync()
    {
        var choice = _withApp.IsChecked == true ? PortableDataChoice.WithApp
            : _userProfile.IsChecked == true ? PortableDataChoice.UserProfile
            : PortableDataChoice.Custom;

        string? storedCustomPath = null;
        if (choice == PortableDataChoice.Custom)
        {
            if (string.IsNullOrWhiteSpace(_customPath.Text))
            {
                ShowMessage("Choose a folder first.", NoticeKind.Error);
                return;
            }

            storedCustomPath = DataLocationResolver.ToStoredCustomPath(_appDirectory, _customPath.Text.Trim());
        }

        var location = DataLocationResolver.ForPortable(_appDirectory, _folders, choice, storedCustomPath);
        if (!DataLocationResolver.IsWritable(location.DataDir))
        {
            ShowMessage($"Orrery can't write to {location.DataDir}. It may be read-only, or protected by the OS. Choose another option.", NoticeKind.Error);
            return;
        }

        if (location.DataDir != _acceptedOpenFolder && OpenFolderWarning(choice, location.DataDir) is { } warning)
        {
            if (!await Dialogs.ConfirmAsync(this, "Other people can change this folder", warning, "Use anyway"))
            {
                return;
            }

            _acceptedOpenFolder = location.DataDir;
        }

        if (!_acceptUnsavedChoice && !PortableMarker.For(choice, storedCustomPath).TrySave(_appDirectory))
        {
            ShowMessage($"Orrery can't save this choice in {_appDirectory}, so it will ask again next time. Your data will still be saved.", NoticeKind.Warning);
            _continue.Content = "Continue anyway";
            _acceptUnsavedChoice = true;
            return;
        }

        Completed?.Invoke(location);
    }

    /// <summary>
    /// Null unless other users of this computer could change the chosen folder. With the app, they could then also
    /// replace Orrery itself, which no ACL on the data folder prevents.
    /// </summary>
    string? OpenFolderWarning(PortableDataChoice choice, string dataDir)
    {
        if (choice == PortableDataChoice.WithApp && FolderAccess.IsOpenToOthers(_appDirectory))
        {
            return $"{FolderAccess.OpenToOthersWarning} They could also replace Orrery itself.\n\n{_appDirectory}";
        }

        return choice != PortableDataChoice.UserProfile && FolderAccess.IsOpenToOthers(dataDir)
            ? $"{FolderAccess.OpenToOthersWarning}\n\n{dataDir}"
            : null;
    }

    void ShowMessage(string text, NoticeKind kind)
    {
        _message.Text = text;
        _message.Kind = kind;
        _message.IsVisible = true;
    }
}
