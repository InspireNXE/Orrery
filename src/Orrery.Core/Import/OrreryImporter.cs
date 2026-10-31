using System.Text.Json;
using Orrery.Core.Connections;
using Orrery.Core.Export;

namespace Orrery.Core.Import;

/// <summary>
/// Imports a file written by Orrery's export (<see cref="ExportFormat.Orrery"/>). Everything gets a new id,
/// so importing into the tree it came from makes copies, not clashes; the old id is kept as the import
/// source, so the preview can offer to skip what's already here.
/// </summary>
public static class OrreryImporter
{
    internal const string SourcePrefix = "orrery:";
    const long MaxFileBytes = 64L * 1024 * 1024;

    public static ImportResult ImportFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, Path.GetFileName(path));
    }

    public static ImportResult Import(Stream json, string sourceName)
    {
        if (json.CanSeek && json.Length > MaxFileBytes)
        {
            throw new ImportException("This file is larger than Orrery imports at once.");
        }

        ConnectionExporter.OrreryFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConnectionExporter.OrreryFile>(json, ConnectionStore.JsonOptions);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new ImportException($"This isn't a readable Orrery export: {e.Message}");
        }

        if (file is not { Format: ConnectionExporter.OrreryFormatName, Root: not null })
        {
            throw new ImportException("This isn't an Orrery export file.");
        }

        if (file.SchemaVersion > ConnectionExporter.OrrerySchemaVersion)
        {
            throw new ImportException("This file was exported by a newer version of Orrery.");
        }

        var nodes = file.Root.Descendants().ToList();
        if (nodes.Contains(null!))
        {
            throw new ImportException("This file has an empty item, so it may be damaged.");
        }

        if (nodes.Count > MRemoteNgImporter.MaxNodes)
        {
            throw new ImportException($"This file has more than {MRemoteNgImporter.MaxNodes:N0} folders and connections, more than Orrery imports at once.");
        }

        // The import lands in a folder of its own, which takes one level.
        if (!ConnectionTree.CanPlace(file.Root, new ConnectionFolder()))
        {
            throw new ImportException($"This file's folders are nested too deeply. Orrery keeps at most {ConnectionTree.MaxFolderDepth} levels, "
                + "including the folder the import goes into.");
        }

        var newIds = new Dictionary<Guid, Guid>();
        foreach (var node in nodes.Prepend(file.Root))
        {
            if (!newIds.TryAdd(node.Id, Guid.NewGuid()))
            {
                throw new ImportException("This file lists the same item twice, so it may be damaged.");
            }
        }

        var warnings = new List<string>();
        var root = (ConnectionFolder)Rebuild(file.Root, newIds, warnings);
        root.Name = $"Imported from Orrery ({DateTime.Now:yyyy-MM-dd})";
        root.ImportSourceId = null;

        var connections = root.Descendants().OfType<ConnectionEntry>().ToList();
        var protocols = connections.GroupBy(e => ProtocolInfo.Label(SettingsResolver.Resolve(e).Protocol.Value))
            .ToDictionary(g => g.Key, g => g.Count());
        var report = new ImportReport(sourceName, root.Descendants().OfType<ConnectionFolder>().Count(), connections.Count, 0, 0,
            protocols, [], [], ImportReport.Shorten(warnings), From: "Orrery");
        return new ImportResult(root, [], report);
    }

    static ConnectionNode Rebuild(ConnectionNode node, Dictionary<Guid, Guid> newIds, List<string> warnings)
    {
        ConnectionNode copy = node switch
        {
            ConnectionEntry entry => new ConnectionEntry { Id = newIds[entry.Id], Host = entry.Host?.Trim() ?? string.Empty, PortForwards = entry.PortForwards?.ToList() },
            ConnectionFolder folder => new ConnectionFolder { Id = newIds[folder.Id], Expanded = folder.Expanded },
            _ => throw new ImportException("This file has an item Orrery doesn't recognise."),
        };
        copy.Name = node.Name ?? string.Empty;
        copy.Notes = node.Notes;
        copy.ImportSourceId = node.ImportSourceId ?? SourcePrefix + node.Id;
        copy.Settings = node.Settings?.Clone() ?? new InheritableSettings();

        // Exports don't carry saved credentials; an id here would point at nothing on this computer.
        if (copy.Settings.CredentialId != InheritableSettings.NoCredential)
        {
            copy.Settings.CredentialId = null;
        }

        if (copy.Settings.JumpHostId is { } jump && jump != InheritableSettings.NoJumpHost)
        {
            if (newIds.TryGetValue(jump, out var newJump))
            {
                copy.Settings.JumpHostId = newJump;
            }
            else
            {
                copy.Settings.JumpHostId = null;
                warnings.Add($"{copy.Name}: its jump host isn't in the file, so it was left out.");
            }
        }

        if (node is ConnectionFolder source && copy is ConnectionFolder target)
        {
            foreach (var child in source.Children)
            {
                target.ChildList.Add(Rebuild(child, newIds, warnings));
            }
        }

        return copy;
    }
}
