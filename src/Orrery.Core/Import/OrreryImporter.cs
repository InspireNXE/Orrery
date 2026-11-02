using System.Security.Cryptography;
using System.Text.Json;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Network;

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
    const int MaxKeyFilePathLength = 1024;

    public static ImportResult ImportFile(string path, string? password = null)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, Path.GetFileName(path), password);
    }

    /// <param name="password">Opens an export sealed because it holds saved passwords and keys (<see cref="SealedExport"/>).</param>
    /// <exception cref="MasterPasswordRequiredException">The file is sealed, and <paramref name="password"/> is missing or wrong.</exception>
    public static ImportResult Import(Stream json, string sourceName, string? password = null)
    {
        if (json.CanSeek && json.Length > MaxFileBytes)
        {
            throw new ImportException("This file is larger than Orrery imports at once.");
        }

        using var document = Parse(() => JsonDocument.Parse(json, DocumentOptions));
        if (!SealedExport.IsSealed(document.RootElement))
        {
            return Read(document.RootElement, sourceName, fromSealed: false);
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new MasterPasswordRequiredException(wrongPassword: false);
        }

        byte[]? plaintext;
        try
        {
            var sealedFile = document.RootElement.Deserialize<SealedExport.SealedFile>(ConnectionStore.JsonOptions)
                ?? throw new InvalidDataException("This encrypted export is damaged.");
            plaintext = SealedExport.Open(sealedFile, password);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidDataException or FormatException)
        {
            throw new ImportException(e is InvalidDataException ? e.Message : "This encrypted export is damaged.");
        }

        if (plaintext is null)
        {
            throw new MasterPasswordRequiredException(wrongPassword: true);
        }

        try
        {
            using var inner = Parse(() => JsonDocument.Parse(plaintext, DocumentOptions));
            return Read(inner.RootElement, sourceName, fromSealed: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // As deep as connections.json may nest, which a deep folder tree needs.
    static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = ConnectionStore.JsonOptions.MaxDepth };

    static JsonDocument Parse(Func<JsonDocument> parse)
    {
        try
        {
            return parse();
        }
        catch (JsonException e)
        {
            throw new ImportException($"This isn't a readable Orrery export: {e.Message}");
        }
    }

    /// <param name="fromSealed">Only a sealed file's saved credentials are taken; a plain export never has any.</param>
    static ImportResult Read(JsonElement json, string sourceName, bool fromSealed)
    {
        ConnectionExporter.OrreryFile? file;
        try
        {
            file = json.Deserialize<ConnectionExporter.OrreryFile>(ConnectionStore.JsonOptions);
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

        // Saved credentials get new ids too, and connections follow them.
        var credentialIds = new Dictionary<Guid, Guid>();
        var credentials = new List<Credential>();
        if (fromSealed && file.Credentials is { } carried)
        {
            if (carried.Count > MRemoteNgImporter.MaxCredentials)
            {
                throw new ImportException($"This file has more than {MRemoteNgImporter.MaxCredentials:N0} saved credentials, more than Orrery imports at once.");
            }

            foreach (var credential in carried)
            {
                if (credential is not { Secret: not null } || !Enum.IsDefined(credential.Kind) || !credentialIds.TryAdd(credential.Id, Guid.NewGuid()))
                {
                    throw new ImportException("This file has a saved credential Orrery can't read, so it may be damaged.");
                }

                credentials.Add(credential with
                {
                    Id = credentialIds[credential.Id],
                    Name = string.IsNullOrWhiteSpace(credential.Name) ? "Imported credential" : credential.Name.Trim(),
                });
            }
        }

        var warnings = new List<string>();
        var root = (ConnectionFolder)Rebuild(file.Root, newIds, credentialIds, warnings);
        root.Name = $"Imported from Orrery ({DateTime.Now:yyyy-MM-dd})";
        root.ImportSourceId = null;

        // Links each node to its folder (and each guest to its host), so settings resolve before it's added.
        _ = new ConnectionTree(root);

        var connections = root.Descendants().OfType<ConnectionEntry>().ToList();
        var protocols = connections.GroupBy(e => ProtocolInfo.Label(SettingsResolver.Resolve(e).Protocol.Value))
            .ToDictionary(g => g.Key, g => g.Count());
        var report = new ImportReport(sourceName, root.Descendants().OfType<ConnectionFolder>().Count(), connections.Count, credentials.Count, 0,
            protocols, [], [], ImportReport.Shorten(warnings), From: "Orrery");
        return new ImportResult(root, credentials, report);
    }

    static ConnectionNode Rebuild(ConnectionNode node, Dictionary<Guid, Guid> newIds, Dictionary<Guid, Guid> credentialIds, List<string> warnings)
    {
        ConnectionNode copy = node switch
        {
            ConnectionEntry entry => new ConnectionEntry { Id = newIds[entry.Id], Host = entry.Host?.Trim() ?? string.Empty, PortForwards = entry.PortForwards?.ToList() },
            ConnectionFolder folder => new ConnectionFolder { Id = newIds[folder.Id], Expanded = folder.Expanded },
            _ => throw new ImportException("This file has an item Orrery doesn't recognize."),
        };
        copy.Name = node.Name ?? string.Empty;
        copy.Notes = node.Notes;
        if (node is ConnectionEntry fromEntry && copy is ConnectionEntry toEntry)
        {
            // Both go into a UDP packet's destination and contents, so anything unreadable is left out, not kept as text.
            if (!string.IsNullOrWhiteSpace(fromEntry.MacAddress))
            {
                if (MacAddress.TryNormalize(fromEntry.MacAddress, out var mac))
                {
                    toEntry.MacAddress = mac;
                }
                else
                {
                    warnings.Add($"{copy.Name}: its MAC address isn't valid, so it was left out.");
                }
            }

            if (!string.IsNullOrWhiteSpace(fromEntry.WakeBroadcast))
            {
                if (WakeOnLan.TryParseBroadcast(fromEntry.WakeBroadcast, out var broadcast))
                {
                    toEntry.WakeBroadcast = broadcast.ToString();
                }
                else
                {
                    warnings.Add($"{copy.Name}: its wake-up broadcast address isn't valid, so it was left out.");
                }
            }

            // Opened by the browser, so only an http or https address is kept.
            if (!string.IsNullOrWhiteSpace(fromEntry.ManagementUrl))
            {
                if (ManagementUrl.TryNormalize(fromEntry.ManagementUrl, out var url))
                {
                    toEntry.ManagementUrl = url;
                }
                else
                {
                    warnings.Add($"{copy.Name}: its management URL isn't an http or https address, so it was left out.");
                }
            }
        }

        copy.ImportSourceId = node.ImportSourceId ?? SourcePrefix + node.Id;
        copy.Settings = node.Settings?.Clone() ?? new InheritableSettings();

        // Only a path, which isn't read until the connection is used; a value that can't be one is left out.
        if (copy.Settings.KeyFile is { } keyFile)
        {
            keyFile = keyFile.Trim();
            bool usable = keyFile.Length <= MaxKeyFilePathLength && !keyFile.Any(char.IsControl);
            copy.Settings.KeyFile = usable ? keyFile : null;
            if (!usable)
            {
                warnings.Add($"{copy.Name}: its key file isn't a usable path, so it was left out.");
            }
        }

        // Only a sealed export carries saved credentials; any other id would point at nothing on this computer.
        if (copy.Settings.CredentialId is { } credentialId && credentialId != InheritableSettings.NoCredential)
        {
            copy.Settings.CredentialId = credentialIds.TryGetValue(credentialId, out var newCredential) ? newCredential : null;
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
                target.ChildList.Add(Rebuild(child, newIds, credentialIds, warnings));
            }
        }
        else if (node is ConnectionEntry { GuestList: { Count: > 0 } guests } original && copy is ConnectionEntry host)
        {
            if (guests.Any(g => g.GuestList is { Count: > 0 }))
            {
                throw new ImportException($"A guest of \"{original.Name}\" has guests of its own, so the file may be damaged.");
            }

            host.GuestsCollapsed = original.GuestsCollapsed;
            host.GuestList = guests.Select(g => (ConnectionEntry)Rebuild(g, newIds, credentialIds, warnings)).ToList();
        }

        return copy;
    }
}
