using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Core.Tests.Storage;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.Core.Tests.Updates;

/// <summary>Updates from a local release list, as build/New-TestRelease.ps1 writes one: no network.</summary>
public class UpdaterTests : IDisposable
{
    static readonly ReleaseVersion Old = ReleaseVersion.Parse("0.1.0");
    const string NewVersion = "0.2.0";
    const string PortableZip = "Orrery-0.2.0-win-x64-portable.zip";
    const string AppImageFile = "Orrery-0.2.0-x86_64.AppImage";

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string P(params string[] parts) => Path.Combine([_root, .. parts]);

    string Feed => P("release", "releases.json");

    string Cache => P("cache");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A release folder holding <paramref name="files"/>, a SHA256SUMS for them (or <paramref name="sums"/>) and a
    /// releases.json naming them by relative path.
    /// </summary>
    void WriteRelease(Dictionary<string, byte[]> files, string? sums = null, Func<string, string?>? digest = null, bool withSums = true)
    {
        Directory.CreateDirectory(P("release"));
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(P("release", name), bytes);
        }

        sums ??= string.Concat(files.Select(f => $"{Hash(f.Value)}  {f.Key}\n"));
        var assets = files.Select(f => new Dictionary<string, object?>
        {
            ["name"] = f.Key,
            ["size"] = f.Value.Length,
            ["browser_download_url"] = f.Key,
            ["digest"] = digest?.Invoke(f.Key),
        }).ToList();
        if (withSums)
        {
            File.WriteAllText(P("release", Release.ChecksumsName), sums);
            assets.Add(new() { ["name"] = Release.ChecksumsName, ["size"] = Encoding.UTF8.GetByteCount(sums), ["browser_download_url"] = Release.ChecksumsName });
        }

        var release = new Dictionary<string, object?>
        {
            ["tag_name"] = "v" + NewVersion,
            ["name"] = "Orrery " + NewVersion,
            ["draft"] = false,
            ["prerelease"] = false,
            ["assets"] = assets,
        };
        File.WriteAllText(Feed, JsonSerializer.Serialize(new[] { release }));
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    static byte[] PortableZipWith(string executable, string contents)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

            Add(executable, contents);
            Add("LICENSE", "new license");
            Add("THIRD-PARTY-NOTICES.txt", "new notices");
            Add(PortableMarker.FileName, "");
        }

        return buffer.ToArray();
    }

    Updater For(ThisCopy copy) => new(copy, Old, new Uri(Feed), Cache);

    /// <summary>A portable app folder with its marker and data, which an update must leave alone.</summary>
    string PortableApp(string executable = "Orrery.exe")
    {
        Directory.CreateDirectory(P("app", "data"));
        File.WriteAllText(P("app", executable), "old app");
        File.WriteAllText(P("app", "LICENSE"), "old license");
        File.WriteAllText(P("app", PortableMarker.FileName), """{"version":1,"dataLocation":"withApp"}""");
        File.WriteAllText(P("app", "data", "settings.json"), "{}");
        File.WriteAllText(P("app", "notes.txt"), "mine");
        return P("app", executable);
    }

    [Fact]
    public async Task Offers_the_newer_release_with_this_copys_file()
    {
        WriteRelease(new() { [PortableZip] = [1, 2, 3], ["Orrery-Setup-0.2.0.exe"] = [4] });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));

        var offer = await updater.CheckAsync(new AppSettings());

        Assert.NotNull(offer);
        Assert.Equal(PortableZip, offer.Asset!.Name);
        Assert.True(offer.CanInstall);
        Assert.Null(await updater.CheckAsync(new AppSettings { SkippedUpdateVersion = NewVersion }));
    }

    [Fact]
    public async Task A_copy_that_doesnt_update_itself_or_a_release_without_sums_only_gets_the_release()
    {
        WriteRelease(new() { [PortableZip] = [1, 2, 3] });
        using (var package = For(new ThisCopy(CopyKind.LinuxPackage, Architecture.X64)))
        {
            Assert.False((await package.CheckAsync(new AppSettings()))!.CanInstall);
        }

        WriteRelease(new() { [PortableZip] = [1, 2, 3] }, withSums: false);
        using var portable = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = await portable.CheckAsync(new AppSettings());
        Assert.False(offer!.CanInstall);
    }

    [Fact]
    public async Task A_missing_release_list_fails_plainly()
    {
        using var updater = For(new ThisCopy(CopyKind.WindowsSetup, Architecture.X64));
        var e = await Assert.ThrowsAsync<UpdateException>(() => updater.CheckAsync(new AppSettings()));
        Assert.Contains("releases.json", e.Message);
    }

    [Fact]
    public async Task Downloads_and_checks_the_file_reporting_progress()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        WriteRelease(new() { [PortableZip] = bytes }, digest: name => "sha256:" + Hash(bytes));
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = (await updater.CheckAsync(new AppSettings()))!;
        var reported = new List<long>();

        var update = await updater.DownloadAsync(offer, new SyncProgress(reported.Add));

        Assert.Equal(P("cache", "updates", NewVersion, PortableZip), update.FilePath);
        Assert.Equal(bytes, File.ReadAllBytes(update.FilePath));
        Assert.Equal(bytes.Length, reported[^1]);
        Assert.Equal([PortableZip], Directory.GetFiles(P("cache", "updates", NewVersion)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_file_that_doesnt_match_its_sum_is_deleted()
    {
        WriteRelease(new() { [PortableZip] = [1, 2, 3] }, sums: $"{Hash([9, 9, 9])}  {PortableZip}\n");
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = (await updater.CheckAsync(new AppSettings()))!;

        var e = await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(offer));

        Assert.Contains("doesn't match its checksum", e.Message);
        Assert.Empty(Directory.GetFiles(P("cache", "updates", NewVersion)));
    }

    [Fact]
    public async Task Sums_and_githubs_digest_must_agree()
    {
        WriteRelease(new() { [PortableZip] = [1, 2, 3] }, digest: _ => "sha256:" + Hash([9]));
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = (await updater.CheckAsync(new AppSettings()))!;

        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(offer));
        Assert.False(Directory.Exists(P("cache", "updates")));
    }

    [Fact]
    public async Task A_file_not_in_the_sums_or_of_the_wrong_size_fails()
    {
        WriteRelease(new() { [PortableZip] = [1, 2, 3] }, sums: $"{Hash([1, 2, 3])}  something-else.zip\n");
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = (await updater.CheckAsync(new AppSettings()))!;
        Assert.Contains("doesn't list", (await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(offer))).Message);

        var bigger = offer with { Asset = offer.Asset! with { Size = 2 } };
        WriteRelease(new() { [PortableZip] = [1, 2, 3] });
        await Assert.ThrowsAsync<UpdateException>(() => updater.DownloadAsync(bigger));
        Assert.Empty(Directory.GetFiles(P("cache", "updates", NewVersion)));
    }

    [Fact]
    public async Task A_file_left_from_before_is_used_only_if_it_still_matches()
    {
        byte[] bytes = [1, 2, 3, 4];
        WriteRelease(new() { [PortableZip] = bytes });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, P("app", "Orrery.exe")));
        var offer = (await updater.CheckAsync(new AppSettings()))!;
        Directory.CreateDirectory(P("cache", "updates", NewVersion));
        File.WriteAllBytes(P("cache", "updates", NewVersion, PortableZip), [6, 6, 6, 6]);

        var update = await updater.DownloadAsync(offer);

        Assert.Equal(bytes, File.ReadAllBytes(update.FilePath));
    }

    [Fact]
    public async Task A_portable_update_swaps_the_executable_and_notices_and_nothing_else()
    {
        var exe = PortableApp();
        WriteRelease(new() { [PortableZip] = PortableZipWith("Orrery.exe", "new app") });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, exe));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);
        var marker = File.ReadAllText(P("app", PortableMarker.FileName));

        var command = updater.Apply(update);

        Assert.Equal(new UpdateCommand(exe, [UpdateCommand.RestartedFlag], RunsSetup: false).FileName, command.FileName);
        Assert.Equal([UpdateCommand.RestartedFlag], command.Arguments);
        Assert.Equal("new app", File.ReadAllText(exe));
        Assert.Equal("old app", File.ReadAllText(P("app", "Orrery.old.exe")));
        Assert.Equal("new license", File.ReadAllText(P("app", "LICENSE")));
        Assert.Equal("new notices", File.ReadAllText(P("app", "THIRD-PARTY-NOTICES.txt")));
        Assert.Equal(marker, File.ReadAllText(P("app", PortableMarker.FileName)));
        Assert.Equal("{}", File.ReadAllText(P("app", "data", "settings.json")));
        Assert.Equal("mine", File.ReadAllText(P("app", "notes.txt")));
        Assert.False(File.Exists(P("app", "Orrery.new.exe")));

        // At the next start, the old executable and the download go.
        using var next = new Updater(updater.Copy, ReleaseVersion.Parse(NewVersion), new Uri(Feed), Cache);
        next.RemoveLeftovers();
        Assert.False(File.Exists(P("app", "Orrery.old.exe")));
        Assert.False(Directory.Exists(P("cache", "updates", NewVersion)));
        Assert.Equal("new app", File.ReadAllText(exe));
        Assert.True(File.Exists(P("app", PortableMarker.FileName)));
    }

    [Fact]
    public async Task A_portable_update_that_fails_part_way_puts_the_old_executable_back()
    {
        var exe = PortableApp();
        // A folder where the notices go makes the step after the swap fail.
        Directory.CreateDirectory(P("app", "THIRD-PARTY-NOTICES.txt"));
        WriteRelease(new() { [PortableZip] = PortableZipWith("Orrery.exe", "new app") });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, exe));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);

        var e = Assert.Throws<UpdateException>(() => updater.Apply(update));

        Assert.Contains("Nothing was changed", e.Message);
        Assert.Equal("old app", File.ReadAllText(exe));
        Assert.False(File.Exists(P("app", "Orrery.old.exe")));
        Assert.False(File.Exists(P("app", "Orrery.new.exe")));
        Assert.True(File.Exists(P("app", PortableMarker.FileName)));
    }

    [Fact]
    public async Task A_zip_without_the_executable_changes_nothing()
    {
        var exe = PortableApp();
        WriteRelease(new() { [PortableZip] = PortableZipWith("Something.exe", "new app") });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, exe));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);

        Assert.Throws<UpdateException>(() => updater.Apply(update));
        Assert.Equal("old app", File.ReadAllText(exe));
        Assert.Equal("old license", File.ReadAllText(P("app", "LICENSE")));
    }

    [Fact]
    public async Task A_download_changed_after_checking_isnt_applied()
    {
        var exe = PortableApp();
        WriteRelease(new() { [PortableZip] = PortableZipWith("Orrery.exe", "new app") });
        using var updater = For(new ThisCopy(CopyKind.WindowsPortable, Architecture.X64, exe));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);
        File.WriteAllBytes(update.FilePath, PortableZipWith("Orrery.exe", "tampered"));

        Assert.Throws<UpdateException>(() => updater.Apply(update));
        Assert.Equal("old app", File.ReadAllText(exe));
    }

    [Fact]
    public async Task An_appimage_update_swaps_the_file_at_appimage()
    {
        Directory.CreateDirectory(P("apps"));
        var appImage = P("apps", "Orrery-0.1.0-x86_64.AppImage");
        File.WriteAllText(appImage, "old image");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(appImage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        WriteRelease(new() { [AppImageFile] = Encoding.UTF8.GetBytes("new image") });
        using var updater = For(new ThisCopy(CopyKind.AppImage, Architecture.X64, appImage));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);

        var command = updater.Apply(update);

        Assert.Equal(appImage, command.FileName);
        Assert.Equal("new image", File.ReadAllText(appImage));
        Assert.Equal("old image", File.ReadAllText(P("apps", "Orrery-0.1.0-x86_64.old.AppImage")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(appImage).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public async Task A_linux_portable_update_keeps_the_executable_bit()
    {
        var exe = PortableApp("Orrery");
        File.SetUnixFileMode(exe, (UnixFileMode)0b111_101_101);
        WriteRelease(new() { ["Orrery-0.2.0-linux-x64-portable.zip"] = PortableZipWith("Orrery", "new app") });
        using var updater = For(new ThisCopy(CopyKind.LinuxPortable, Architecture.X64, exe));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);

        updater.Apply(update);

        Assert.Equal("new app", File.ReadAllText(exe));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(exe));
    }

    [Fact]
    public async Task An_installed_copy_runs_setup_silently_and_is_started_again()
    {
        WriteRelease(new() { ["Orrery-Setup-0.2.0.exe"] = [7, 7] });
        using var updater = For(new ThisCopy(CopyKind.WindowsSetup, Architecture.X64));
        var update = await updater.DownloadAsync((await updater.CheckAsync(new AppSettings()))!);

        var command = updater.Apply(update);

        Assert.True(command.RunsSetup);
        Assert.Equal(update.FilePath, command.FileName);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1"], command.Arguments);
    }

    [Fact]
    public void Leftovers_keep_downloads_of_newer_versions()
    {
        Directory.CreateDirectory(P("cache", "updates", "0.0.9"));
        Directory.CreateDirectory(P("cache", "updates", "0.1.0"));
        Directory.CreateDirectory(P("cache", "updates", "0.3.0"));
        Directory.CreateDirectory(P("cache", "updates", "junk"));
        using var updater = For(new ThisCopy(CopyKind.WindowsSetup, Architecture.X64));

        updater.RemoveLeftovers();

        Assert.Equal(["0.3.0"], Directory.GetDirectories(P("cache", "updates")).Select(Path.GetFileName));
    }

    /// <summary>Reports straight away, unlike Progress&lt;T&gt;, which posts to the thread pool.</summary>
    sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
