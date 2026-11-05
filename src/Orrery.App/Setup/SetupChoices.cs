using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.App.Setup;

/// <summary>Which first-launch setup a start gets, if any.</summary>
enum SetupKind
{
    /// <summary>A data folder that's been used before: straight to the main window.</summary>
    None,

    /// <summary>A portable copy with no data folder chosen yet: setup starts by asking where.</summary>
    WithDataStep,

    /// <summary>A data folder with nothing in it yet: setup without the data question.</summary>
    WithoutDataStep,
}

static class FirstLaunch
{
    /// <summary>
    /// Setup runs once per data folder: for a portable copy still to choose one, or for a folder with no settings and no
    /// connections yet. A folder from before setup existed has one or the other, so its owner isn't asked.
    /// </summary>
    public static SetupKind Decide(StartupStorage storage) => storage switch
    {
        StartupStorage.NeedsPortableChoice => SetupKind.WithDataStep,
        StartupStorage.Ready ready when IsNew(ready.Location) => SetupKind.WithoutDataStep,
        _ => SetupKind.None,
    };

    static bool IsNew(DataLocation location) =>
        !File.Exists(location.FileIn(AppSettingsStore.FileName)) && !File.Exists(location.FileIn("connections.json"));
}

/// <summary>What first-launch setup decided. Each starts at Orrery's own default.</summary>
sealed record SetupChoices
{
    static readonly AppSettings Defaults = new();

    /// <summary>Null where setup didn't ask, which leaves the usual pick: the OS keychain where there is one.</summary>
    public CredentialStoreKind? Passwords { get; init; }

    /// <summary>Files to import once the main window opens, in order, each through its preview.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>The ids of the local shells to add to the tree as LOCAL connections, in their folder.</summary>
    public IReadOnlyList<string> LocalShells { get; init; } = [];

    public bool UpdateChecks { get; init; } = Defaults.ChecksForUpdates;
    public UpdateAction WhenUpdateFound { get; init; } = Defaults.UpdateAction;
    public bool UpdatePreviews { get; init; } = Defaults.OffersPreviewUpdates;
    public bool HostStatusChecks { get; init; } = Defaults.ChecksHostStatus;
    public bool WebPagesInOrrery { get; init; } = Defaults.OpensWebPagesInOrrery;

    /// <summary>
    /// <paramref name="settings"/> with these choices, and setup marked done. A choice left at the default is saved as
    /// unset, as the settings file does everywhere, so a later version can change the default.
    /// </summary>
    public AppSettings ApplyTo(AppSettings settings) => settings with
    {
        CredentialStore = Passwords == CredentialStoreKind.VaultFile ? CredentialStoreKind.VaultFile : null,
        UpdateChecks = UnlessDefault(UpdateChecks, Defaults.ChecksForUpdates),
        WhenUpdateFound = UnlessDefault(WhenUpdateFound, Defaults.UpdateAction),
        UpdatePreviews = UnlessDefault(UpdatePreviews, Defaults.OffersPreviewUpdates),
        HostStatusChecks = UnlessDefault(HostStatusChecks, Defaults.ChecksHostStatus),
        WebPagesInOrrery = UnlessDefault(WebPagesInOrrery, Defaults.OpensWebPagesInOrrery),
        SetupDone = true,
    };

    static T? UnlessDefault<T>(T value, T fallback) where T : struct =>
        EqualityComparer<T>.Default.Equals(value, fallback) ? null : value;
}

/// <summary>A connections file setup found or was given, and what made it.</summary>
sealed record FoundImport(string Path, string Kind);

/// <summary>Connection files from other tools in their usual places, offered for import at first launch.</summary>
static class ImportSources
{
    /// <summary>
    /// The files that exist of mRemoteNG's <c>confCons.xml</c> (under <paramref name="appData"/>, Windows only, so null
    /// elsewhere) and the OpenSSH config in <paramref name="userProfile"/>. Only whether each is there is checked; the
    /// import reads them later, behind its preview.
    /// </summary>
    public static IReadOnlyList<FoundImport> Find(string userProfile, string? appData)
    {
        var found = new List<FoundImport>();
        if (appData is { Length: > 0 } && Path.Combine(appData, "mRemoteNG", "confCons.xml") is var mRemoteNg && File.Exists(mRemoteNg))
        {
            found.Add(new FoundImport(mRemoteNg, "mRemoteNG"));
        }

        if (userProfile is { Length: > 0 } && Path.Combine(userProfile, ".ssh", "config") is var ssh && File.Exists(ssh))
        {
            found.Add(new FoundImport(ssh, "SSH config"));
        }

        return found;
    }

    /// <summary><see cref="Find"/> for this user on this computer.</summary>
    public static IReadOnlyList<FoundImport> OnThisComputer() =>
        Find(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null);

    /// <summary>
    /// Whether importing the file will ask for a password: an mRemoteNG file with a master password of its own, or an
    /// Orrery export sealed because it holds saved passwords. Reads the file, so not on the UI thread.
    /// </summary>
    public static bool NeedsPassword(string path) =>
        !RdpFileImporter.IsRdpFile(path) && ConnectionExporter.FormatFor(path) switch
        {
            ExportFormat.MRemoteNg => MRemoteNgImporter.NeedsMasterPassword(path),
            ExportFormat.Orrery => OrreryImporter.IsSealedFile(path),
            _ => false,
        };

    /// <summary>What a chosen file is, by its name, as Import will read it.</summary>
    public static string KindOf(string path) =>
        RdpFileImporter.IsRdpFile(path) ? "Remote Desktop file" : ConnectionExporter.FormatFor(path) switch
        {
            ExportFormat.MRemoteNg => "mRemoteNG",
            ExportFormat.Orrery => "Orrery export",
            _ => "SSH config",
        };
}

/// <summary>The folder of local shells setup adds to the tree.</summary>
static class LocalShellsFolder
{
    public const string Name = "Local";

    /// <summary>
    /// Adds a folder with a LOCAL connection for each of <paramref name="shells"/> (id and name) at the top of the tree.
    /// Nothing when there are none, or when the tree already has a LOCAL connection (a data folder set up before).
    /// Setup runs once per data folder, so a shell deleted later isn't added back.
    /// </summary>
    public static ConnectionFolder? AddTo(ConnectionTree tree, IReadOnlyList<(string Id, string Name)> shells)
    {
        if (shells.Count == 0 || tree.AllConnections().Any(e => e.Settings.Protocol == Protocol.Local))
        {
            return null;
        }

        var folder = new ConnectionFolder { Name = Name };
        tree.Add(folder, tree.Root, 0);
        foreach (var (id, name) in shells)
        {
            tree.Add(new ConnectionEntry { Name = name, Host = id, Settings = new InheritableSettings { Protocol = Protocol.Local } }, folder);
        }

        return folder;
    }
}
