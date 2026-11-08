using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using static Orrery.App.Connections.PropertyEditors;

namespace Orrery.App.Connections;

/// <summary>RDP: where the remote computer's sound goes, the drives and folders it sees, and the console session.</summary>
sealed class RdpSection : PropertiesSection
{
    const int MaxPathLength = 1024;

    readonly ComboBox _remoteAudio = Choices("RemoteAudio", (RemoteAudio.PlayHere, "Play on this computer"), (RemoteAudio.DoNotPlay, "Don't play"));
    readonly ToggleSwitch _shareAllDrives = Switch("ShareAllDrives");
    readonly TextBox _sharedFolders = new()
    {
        AcceptsReturn = true, MinHeight = 56, FontFamily = Dialogs.MonoFont,
        PlaceholderText = OperatingSystem.IsWindows() ? "~/Documents\nD:\\Projects" : "~/Documents\n/Volumes/Projects",
    };

    readonly ToggleSwitch _adminSession = Switch("AdminSession");
    readonly InheritRow _remoteAudioRow, _shareAllDrivesRow, _sharedFoldersRow, _adminSessionRow;
    List<string>? _savedFolders;

    public RdpSection(PropertiesContext context) : base(context)
    {
        _remoteAudioRow = new InheritRow("Remote audio", _remoteAudio, () => Context.ShowInherited(r => Choose(_remoteAudio, r.RemoteAudio.Value)));
        _remoteAudioRow.Tip("RDP: play the remote computer's sound here, or not at all. Applies to new connections.");
        _shareAllDrivesRow = new InheritRow("Share every drive", _shareAllDrives, () => Context.ShowInherited(r => _shareAllDrives.IsChecked = r.ShareAllDrives.Value));
        _shareAllDrivesRow.Tip("RDP: every drive on this computer shows on the remote one, under This PC, and the remote computer can read and change their files. "
            + "On Linux and macOS it's your home folder.");
        var browseFolder = new Button { Content = "Add folder…", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left };
        browseFolder.Click += async (_, _) => await AddSharedFolderAsync();
        AutomationProperties.SetName(browseFolder, "Add a shared folder");
        AutomationProperties.SetName(_sharedFolders, "Shared folders");
        _sharedFoldersRow = new InheritRow("Shared folders", new StackPanel { Spacing = 6, Children = { _sharedFolders, browseFolder } },
            () => Context.ShowInherited(r => _sharedFolders.Text = string.Join("\n", r.SharedFolders.Value)));
        _sharedFoldersRow.Tip("RDP: folders on this computer to show on the remote one as drives, one per line; ~ means your home folder. "
            + "The remote computer can read and change their files. Leave it empty to share none.");
        _adminSessionRow = new InheritRow("Connect to the console session", _adminSession, () => Context.ShowInherited(r => _adminSession.IsChecked = r.AdminSession.Value));
        _adminSessionRow.Tip("RDP: the server's console (administrative) session, as mstsc /admin, for administering a server without a Remote Desktop license.");
        View = Pane.RowSection("RDP", _remoteAudioRow.View, _shareAllDrivesRow.View, _sharedFoldersRow.View, _adminSessionRow.View);
    }

    public override Control View { get; }

    public override IEnumerable<InheritRow> Rows => [_remoteAudioRow, _shareAllDrivesRow, _sharedFoldersRow, _adminSessionRow];

    public override IEnumerable<Control> Fields => [_remoteAudio, _shareAllDrives, _sharedFolders, _adminSession];

    public override void Load(ConnectionNode node, InheritableSettings own, ResolvedSettings inherited)
    {
        _remoteAudioRow.Load(own.RemoteAudio is null, Describe(inherited.RemoteAudio));
        Choose(_remoteAudio, own.RemoteAudio ?? inherited.RemoteAudio.Value);
        _shareAllDrivesRow.Load(own.ShareAllDrives is null, Describe(inherited.ShareAllDrives));
        _shareAllDrives.IsChecked = own.ShareAllDrives ?? inherited.ShareAllDrives.Value;
        _sharedFoldersRow.Load(own.SharedFolders is null, Describe(inherited.SharedFolders));
        _sharedFolders.Text = string.Join("\n", own.SharedFolders ?? inherited.SharedFolders.Value);
        _adminSessionRow.Load(own.AdminSession is null, Describe(inherited.AdminSession));
        _adminSession.IsChecked = own.AdminSession ?? inherited.AdminSession.Value;
    }

    public override string? Save(InheritableSettings settings)
    {
        _savedFolders = null;
        if (!_sharedFoldersRow.Inheriting)
        {
            // Only paths, which can't be checked here: a folder may be on a drive that isn't plugged in now.
            _savedFolders = (_sharedFolders.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            if (_savedFolders.Any(f => f.Length > MaxPathLength || f.Any(char.IsControl)))
            {
                return "A shared folder's path is too long or has characters a path can't have.";
            }
        }

        settings.RemoteAudio = _remoteAudioRow.Inheriting ? null : Chosen<RemoteAudio>(_remoteAudio);
        settings.ShareAllDrives = _shareAllDrivesRow.Inheriting ? null : _shareAllDrives.IsChecked == true;
        settings.SharedFolders = _savedFolders;
        settings.AdminSession = _adminSessionRow.Inheriting ? null : _adminSession.IsChecked == true;
        return null;
    }

    /// <summary>Adds a folder from the folder picker to the shared folders, written with ~ when it's under the home folder.</summary>
    async Task AddSharedFolderAsync()
    {
        if (Context.TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to share", AllowMultiple = false });
        if (folders.Count == 0)
        {
            return;
        }

        if (folders[0].TryGetLocalPath() is not { } path)
        {
            Context.Fail("That folder isn't on this computer, so it can't be shared.");
            return;
        }

        var text = _sharedFolders.Text?.TrimEnd() ?? string.Empty;
        _sharedFolders.Text = (text.Length > 0 ? text + "\n" : string.Empty) + WithHome(path.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : path);
    }
}
