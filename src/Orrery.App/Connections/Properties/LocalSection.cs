using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using static Orrery.App.Connections.PropertyEditors;

namespace Orrery.App.Connections;

/// <summary>A local shell: where it starts, and on Windows whether it runs as administrator. Set on the connection only.</summary>
sealed class LocalSection : PropertiesSection
{
    readonly TextBox _startFolder = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "~" };
    readonly ToggleSwitch _runAsAdmin = Switch("RunAsAdministrator");

    public LocalSection(PropertiesContext context) : base(context)
    {
        var browseStart = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), MinWidth = 0 };
        browseStart.Click += async (_, _) => await BrowseStartFolderAsync();
        AutomationProperties.SetName(browseStart, "Browse for the starting folder");
        AutomationProperties.SetName(_startFolder, "Starting folder");
        ToolTip.SetTip(_startFolder, "Where the shell starts; empty or ~ is your home folder. A WSL shell starts in its own home folder.");
        AutomationProperties.SetName(_runAsAdmin, "Run as administrator");
        View = Pane.Section("Local terminal",
            Dialogs.Field("Starting folder", new DockPanel { Children = { Pane.Docked(browseStart, Dock.Right), _startFolder } }),
            new StackPanel
            {
                IsVisible = OperatingSystem.IsWindows(),
                Spacing = 4,
                Children =
                {
                    new DockPanel { Children = { Pane.Docked(_runAsAdmin, Dock.Right), new TextBlock { Text = "Run as administrator", VerticalAlignment = VerticalAlignment.Center } } },
                    Hint("Windows asks each time it opens. The tab is marked Administrator, and isn't reopened when Orrery next starts."),
                },
            });
    }

    public override Control View { get; }

    public override bool ForFolders => false;

    public override IEnumerable<Control> Fields => [_startFolder, _runAsAdmin];

    public override void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited)
    {
        _startFolder.Text = (node as ConnectionEntry)?.StartFolder;
        _runAsAdmin.IsChecked = (node as ConnectionEntry)?.RunAsAdministrator == true;
    }

    public override void SaveEntry(ConnectionEntry entry)
    {
        entry.StartFolder = string.IsNullOrWhiteSpace(_startFolder.Text) ? null : _startFolder.Text.Trim();
        entry.RunAsAdministrator = _runAsAdmin.IsChecked == true && OperatingSystem.IsWindows();
    }

    /// <summary>Fills the starting folder from the folder picker, with ~ for the home folder.</summary>
    async Task BrowseStartFolderAsync()
    {
        if (Context.TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose where the shell starts", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _startFolder.Text = WithHome(path.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : path);
        }
    }
}
