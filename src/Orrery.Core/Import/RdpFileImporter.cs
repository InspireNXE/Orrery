using System.Globalization;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;

namespace Orrery.Core.Import;

/// <summary>
/// Reads Remote Desktop Connection's <c>.rdp</c> files: one <c>key:type:value</c> setting a line, where the type is
/// <c>s</c> (text), <c>i</c> (a number) or <c>b</c> (hex bytes). Keys are matched without regard to case, and lines
/// that don't fit the pattern are skipped. mstsc saves these files as UTF-16; any encoding with a byte order mark is
/// read. Nothing in the file is run, and a saved password (<c>password 51</c>, encrypted for one Windows user) isn't
/// imported.
/// </summary>
public static class RdpFileImporter
{
    /// <summary>Marks a connection imported from a .rdp file, followed by the file's full path.</summary>
    public const string SourcePrefix = "rdpfile:";

    /// <summary>Larger than any real .rdp file, which is a few kilobytes.</summary>
    const long MaxFileBytes = 1024 * 1024;

    /// <summary>Whether <paramref name="path"/> is named like a Remote Desktop file.</summary>
    public static bool IsRdpFile(string path) => Path.GetExtension(path).Equals(".rdp", StringComparison.OrdinalIgnoreCase);

    public static ImportResult ImportFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (new FileInfo(full).Length > MaxFileBytes)
        {
            throw new ImportException("This file is far larger than a Remote Desktop file would be.");
        }

        return Import(File.ReadAllText(full), Path.GetFileName(full), Path.GetFileNameWithoutExtension(full), SourcePrefix + full);
    }

    /// <param name="name">The connection's name, usually the file's name without .rdp.</param>
    /// <param name="sourceId">Marks the connection so importing the same file again is spotted; null for none.</param>
    public static ImportResult Import(string text, string sourceName, string name, string? sourceId = null)
    {
        var settings = Parse(text);
        var warnings = new List<string>();
        var unsupported = new List<string>();
        var entry = ToEntry(settings, name, warnings, unsupported);
        entry.ImportSourceId = sourceId;

        var root = new ConnectionFolder { Name = $"Imported from .rdp files ({DateTime.Now:yyyy-MM-dd})" };
        var tree = new ConnectionTree(root);
        tree.Add(entry, root);
        var report = new ImportReport(sourceName, 0, 1, 0, 0, new Dictionary<string, int> { [ProtocolInfo.Label(Protocol.Rdp)] = 1 },
            [], unsupported, warnings, From: "Remote Desktop file");
        return new ImportResult(root, [], report);
    }

    /// <summary>The file's settings by key, in lower case. A key given twice keeps its last value, as mstsc does.</summary>
    public static IReadOnlyDictionary<string, RdpSetting> Parse(string text)
    {
        var settings = new Dictionary<string, RdpSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim('\uFEFF', ' ', '\t', '\0');
            int first = line.IndexOf(':');
            if (first <= 0 || line.Length < first + 3 || line[first + 2] != ':')
            {
                continue;
            }

            char type = char.ToLowerInvariant(line[first + 1]);
            if (type is not ('s' or 'i' or 'b'))
            {
                continue;
            }

            var key = line[..first].Trim().ToLowerInvariant();
            settings[key] = new RdpSetting(type, line[(first + 3)..]);
        }

        return settings;
    }

    /// <summary>A connection with what Orrery can use from <paramref name="settings"/>. Values equal to Orrery's defaults aren't set.</summary>
    public static ConnectionEntry ToEntry(IReadOnlyDictionary<string, RdpSetting> settings, string name, List<string> warnings, List<string> unsupported)
    {
        string? Text(string key) => settings.TryGetValue(key, out var s) && s.Type == 's' && s.Value.Trim().Length > 0 ? s.Value.Trim() : null;
        int? Number(string key) => settings.TryGetValue(key, out var s) && s.Type == 'i'
            && int.TryParse(s.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        bool On(string key) => Number(key) is { } n && n != 0;

        var entry = new ConnectionEntry { Name = string.IsNullOrWhiteSpace(name) ? "Remote Desktop" : name.Trim() };
        var own = entry.Settings;
        own.Protocol = Protocol.Rdp;

        var address = Text("full address") ?? Text("alternate full address");
        if (address is null || !HostAddress.TryParse(address, 0, out var host, out var port))
        {
            warnings.Add("The file doesn't say which computer to connect to.");
        }
        else
        {
            entry.Host = host;
            if (port == 0 && Number("server port") is { } serverPort and >= 1 and <= 65535)
            {
                port = serverPort;
            }

            if (port is >= 1 and <= 65535 && port != ProtocolInfo.DefaultPort(Protocol.Rdp))
            {
                own.Port = port;
            }
        }

        if (Text("username") is { } user)
        {
            // DOMAIN\user is split; user@domain is a user principal name and logs in as it is.
            int slash = user.IndexOf('\\');
            if (slash > 0)
            {
                own.Domain = user[..slash];
                own.Username = user[(slash + 1)..];
            }
            else
            {
                own.Username = user;
            }
        }

        if (Text("domain") is { } domain)
        {
            own.Domain = domain;
        }

        if (settings.ContainsKey("password 51"))
        {
            warnings.Add("The saved password wasn't imported: Windows encrypts it for one user on one computer. Orrery asks for it when you connect.");
        }

        if (Number("redirectclipboard") is { } clipboard && clipboard == 0)
        {
            own.ShareClipboard = false;
        }

        // A fixed size only when the window keeps it: full screen and dynamic resolution follow the screen or window.
        if (Number("screen mode id") == 1 && Number("dynamic resolution") == 0 && Number("desktopwidth") is { } width && Number("desktopheight") is { } height)
        {
            if (DesktopResolution.TryParse($"{width}x{height}", out var size, out _) && size is { } fixedSize)
            {
                own.DesktopSize = $"{fixedSize.Width}x{fixedSize.Height}";
            }
        }

        if (QualityFrom(Number) is { } quality && quality != DisplayQuality.Balanced)
        {
            own.DisplayQuality = quality;
        }

        switch (Number("audiomode"))
        {
            case 1:
                own.RemoteAudio = RemoteAudio.DoNotPlay;
                unsupported.Add("Sound left playing on the remote computer: it isn't played on either");
                break;
            case 2:
                own.RemoteAudio = RemoteAudio.DoNotPlay;
                break;
        }

        if (Text("drivestoredirect") is { } drives)
        {
            var parts = drives.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Contains("*"))
            {
                own.ShareAllDrives = true;
            }
            else
            {
                // Drive letters such as C:\, kept as folders; "DynamicDrives" (drives plugged in later) can't be followed.
                var folders = parts.Where(p => !p.Equals("DynamicDrives", StringComparison.OrdinalIgnoreCase)).Select(DriveAsFolder).ToList();
                if (folders.Count > 0)
                {
                    own.SharedFolders = folders;
                }

                if (folders.Count < parts.Length)
                {
                    unsupported.Add("Sharing drives plugged in later");
                }
            }
        }
        else if (On("redirectdrives"))
        {
            own.ShareAllDrives = true;
        }

        if (On("use multimon"))
        {
            own.UseAllMonitors = true;
            if (Text("selectedmonitors") is not null)
            {
                warnings.Add("The file chooses some of this computer's displays; Orrery spreads the session over all of them in full screen.");
            }
        }

        if (On("administrative session") || On("connect to console"))
        {
            own.AdminSession = true;
        }

        if (Number("enablecredsspsupport") == 0)
        {
            warnings.Add("The file turns off Network Level Authentication, which Orrery always uses. If the server doesn't support it, connecting fails.");
        }

        void Unsupported(string label, bool used)
        {
            if (used) unsupported.Add(label);
        }

        Unsupported("RD Gateway", Text("gatewayhostname") is not null && Number("gatewayusagemethod") is not (null or 0 or 4));
        Unsupported("RemoteApp (the program is opened as a full desktop instead)", On("remoteapplicationmode"));
        Unsupported("Load balance info", Text("loadbalanceinfo") is not null);
        Unsupported("Printer redirection", On("redirectprinters"));
        Unsupported("Smart card redirection", On("redirectsmartcards"));
        Unsupported("Port redirection", On("redirectcomports"));
        Unsupported("Microphone redirection", On("audiocapturemode"));
        Unsupported("Device redirection", Text("devicestoredirect") is not null || Text("usbdevicestoredirect") is not null || Text("camerastoredirect") is not null);
        Unsupported("KDC proxy", Text("kdcproxyname") is not null);
        return entry;
    }

    /// <summary>A drive named by its letter ("C", "C:") as the folder at its top ("C:\\"); anything else as it is.</summary>
    internal static string DriveAsFolder(string drive) =>
        drive.Length is 1 or 2 && char.IsAsciiLetter(drive[0]) && (drive.Length == 1 || drive[1] == ':') ? $"{char.ToUpperInvariant(drive[0])}:\\" : drive;

    /// <summary>
    /// The display quality the file's visual effects stand for, read as the mRemoteNG import reads them: every effect
    /// on is Best, none is Fastest, a mix is Balanced. Null when the file sets none of them.
    /// </summary>
    static DisplayQuality? QualityFrom(Func<string, int?> number)
    {
        var effects = new List<bool>();
        void Effect(string key, bool onMeansEffect)
        {
            if (number(key) is { } n)
            {
                effects.Add((n != 0) == onMeansEffect);
            }
        }

        Effect("disable wallpaper", onMeansEffect: false);
        Effect("disable themes", onMeansEffect: false);
        Effect("allow font smoothing", onMeansEffect: true);
        Effect("allow desktop composition", onMeansEffect: true);
        return effects.Count == 0 ? null
            : effects.All(on => on) ? DisplayQuality.Best
            : effects.Any(on => on) ? DisplayQuality.Balanced
            : DisplayQuality.Fastest;
    }
}

/// <summary>One line of a .rdp file: its type (s, i or b) and the text after the second colon.</summary>
public readonly record struct RdpSetting(char Type, string Value);
