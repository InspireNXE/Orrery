using System.Text.Json.Nodes;
using Orrery.Core.Credentials;
using Orrery.Core.Security;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

/// <summary>The vault, settings and trusted keys when another computer saves them through a sync app.</summary>
public class SharedFileMergeTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);

    readonly string _dir = Directory.CreateTempSubdirectory("orrery-shared-").FullName;

    string In(string name) => Path.Combine(_dir, name);

    static Credential Password(string name) => new(Guid.NewGuid(), name, CredentialKind.Password, "admin", null, "dummy-" + name);

    // The file's time moves on with each save, as it would between two computers' saves.
    void Later(string path) => File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

    [Fact]
    public void JSON_merge_takes_each_side_s_changes_and_keeps_mine_where_both_changed()
    {
        var baseline = JsonNode.Parse("""{ "a": 1, "b": 1, "c": 1, "o": { "x": 1, "y": 1 }, "gone": 1 }""")!.AsObject();
        var mine = JsonNode.Parse("""{ "a": 2, "b": 1, "c": 5, "o": { "x": 2, "y": 1 }, "gone": 1, "mine": true }""")!.AsObject();
        var theirs = JsonNode.Parse("""{ "a": 1, "b": 3, "c": 6, "o": { "x": 1, "y": 3 }, "theirs": true }""")!.AsObject();
        var conflicts = new List<string>();

        var merged = JsonMerge.Merge(baseline, mine, theirs, conflicts);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "a": 2, "b": 3, "c": 5, "o": { "x": 2, "y": 3 }, "mine": true, "theirs": true }"""), merged));
        Assert.Equal(["c"], conflicts);
    }

    [Fact]
    public async Task Credentials_saved_on_each_computer_are_all_in_the_vault()
    {
        using (var setup = new VaultFileStore(In("vault.frv"), FastKdf))
        {
            await setup.UnlockAsync("a long master password");
            await setup.SaveAsync(Password("shared"));
        }

        using var laptop = new VaultFileStore(In("vault.frv"), FastKdf);
        using var desktop = new VaultFileStore(In("vault.frv"), FastKdf);
        await laptop.UnlockAsync("a long master password");
        await desktop.UnlockAsync("a long master password");

        var shared = (await laptop.ListAsync()).Single();
        await laptop.SaveAsync(Password("laptop"));
        await laptop.DeleteAsync(shared.Id);
        Later(In("vault.frv"));
        await desktop.SaveAsync(Password("desktop"));

        Assert.Equal(["desktop", "laptop"], (await desktop.ListAsync()).Select(c => c.Name));
        Later(In("vault.frv"));
        Assert.Equal(["desktop", "laptop"], (await laptop.ListAsync()).Select(c => c.Name));

        using var reopened = new VaultFileStore(In("vault.frv"), FastKdf);
        await reopened.UnlockAsync("a long master password");
        Assert.Equal(["desktop", "laptop"], (await reopened.ListAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task A_master_password_changed_on_the_other_computer_locks_the_vault_here()
    {
        using var laptop = new VaultFileStore(In("vault.frv"), FastKdf);
        await laptop.UnlockAsync("a long master password");
        await laptop.SaveAsync(Password("shared"));
        using var desktop = new VaultFileStore(In("vault.frv"), FastKdf);
        await desktop.UnlockAsync("a long master password");
        bool locked = false;
        desktop.AutoLocked += (_, _) => locked = true;

        await laptop.ChangePasswordAsync("a long master password", "a new long password");
        Later(In("vault.frv"));

        await Assert.ThrowsAsync<VaultLockedException>(() => desktop.SaveAsync(Password("desktop")));
        Assert.True(locked);
        await desktop.UnlockAsync("a new long password");
        await desktop.SaveAsync(Password("desktop"));
        Assert.Equal(["desktop", "shared"], (await desktop.ListAsync()).Select(c => c.Name));
    }

    [Fact]
    public void A_setting_changed_on_the_other_computer_isn_t_saved_over_by_a_different_one_here()
    {
        var store = new AppSettingsStore(In("settings.json"));
        store.Save(new AppSettings());
        var here = store.Load();

        // The other computer, through the sync app.
        var text = File.ReadAllText(In("settings.json"));
        File.WriteAllText(In("settings.json"), JsonNode.Parse(text)!.AsObject().Also(o => o["terminalFontSize"] = 18).ToJsonString());

        store.Save(here with { FfmpegPath = "C:\\tools\\ffmpeg.exe" });

        var saved = store.Load();
        Assert.Equal(18, saved.TerminalFontSize);
        Assert.Equal("C:\\tools\\ffmpeg.exe", saved.FfmpegPath);
    }

    [Fact]
    public void Hosts_trusted_on_each_computer_are_all_kept()
    {
        var laptop = new JsonCertificateTrustStore(In("known-hosts.json"));
        var desktop = new JsonCertificateTrustStore(In("known-hosts.json"));
        var pin = new PinnedCertificate("ab:cd", "CN=test", DateTimeOffset.UtcNow);

        laptop.Pin("laptop-only", 22, pin);
        Later(In("known-hosts.json"));
        desktop.Pin("desktop-only", 22, pin);

        var fresh = new JsonCertificateTrustStore(In("known-hosts.json"));
        Assert.NotNull(fresh.Get("laptop-only", 22));
        Assert.NotNull(fresh.Get("desktop-only", 22));
        Later(In("known-hosts.json"));
        Assert.NotNull(laptop.Get("desktop-only", 22));
    }

    [Fact]
    public async Task The_watcher_says_when_a_shared_file_changes()
    {
        File.WriteAllText(In("connections.json"), "{}");
        using var watcher = new DataFolderWatcher(_dir, ["connections.json"]);
        var seen = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += names => seen.TrySetResult(names);

        File.WriteAllText(In("notes.txt"), "not watched");
        File.WriteAllText(In("connections.json"), "{ \"changed\": true }");

        var names = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["connections.json"], names);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

static class JsonObjectTestExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
