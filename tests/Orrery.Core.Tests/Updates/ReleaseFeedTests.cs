using Orrery.Core.Settings;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.Core.Tests.Updates;

public class ReleaseFeedTests
{
    static readonly Uri GitHubFeed = ReleaseFeed.GitHub;
    static readonly ReleaseVersion Current = ReleaseVersion.Parse("0.1.0");

    static Release R(string version, bool prerelease = false) =>
        new(ReleaseVersion.Parse(version), "v" + version, version, "", null, prerelease, null, []);

    [Fact]
    public void Reads_github_releases_ignoring_fields_it_doesnt_use()
    {
        const string Json = """
            [
              {
                "url": "https://api.github.com/repos/InspireNXE/Orrery/releases/1",
                "id": 1,
                "tag_name": "v0.2.0",
                "name": "Orrery 0.2.0",
                "body": "What's new",
                "html_url": "https://github.com/InspireNXE/Orrery/releases/tag/v0.2.0",
                "draft": false,
                "prerelease": false,
                "published_at": "2026-11-02T10:00:00Z",
                "author": { "login": "someone" },
                "assets": [
                  {
                    "name": "Orrery-Setup-0.2.0.exe",
                    "size": 1234,
                    "content_type": "application/x-msdownload",
                    "browser_download_url": "https://github.com/InspireNXE/Orrery/releases/download/v0.2.0/Orrery-Setup-0.2.0.exe",
                    "digest": "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
                  },
                  {
                    "name": "SHA256SUMS",
                    "size": 99,
                    "browser_download_url": "https://github.com/InspireNXE/Orrery/releases/download/v0.2.0/SHA256SUMS"
                  }
                ]
              }
            ]
            """;

        var release = Assert.Single(ReleaseFeed.Parse(Json, GitHubFeed));

        Assert.Equal(ReleaseVersion.Parse("0.2.0"), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("Orrery 0.2.0", release.Name);
        Assert.Equal("What's new", release.Notes);
        Assert.Equal(new Uri("https://github.com/InspireNXE/Orrery/releases/tag/v0.2.0"), release.Page);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:00:00Z"), release.PublishedAt);
        Assert.Equal(2, release.Assets.Count);
        var setup = release.Asset("Orrery-Setup-0.2.0.exe")!;
        Assert.Equal(1234, setup.Size);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", setup.Sha256);
        Assert.Null(release.Checksums!.Sha256);
    }

    [Fact]
    public void Leaves_out_drafts_and_tags_that_arent_versions()
    {
        const string Json = """
            [
              { "tag_name": "v0.3.0", "draft": true, "assets": [] },
              { "tag_name": "nightly", "assets": [] },
              { "tag_name": "v0.2.0", "assets": [] },
              { "name": "no tag" }
            ]
            """;

        var release = Assert.Single(ReleaseFeed.Parse(Json, GitHubFeed));
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("v0.2.0", release.Name);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"message\": \"Not Found\" }")]
    [InlineData("[ { \"tag_name\": \"v1.0.0\", \"assets\": [ { \"size\": \"big\" } ] } ]")]
    public void A_list_that_cant_be_read_fails_plainly(string json)
    {
        var e = Assert.Throws<UpdateException>(() => ReleaseFeed.Parse(json, GitHubFeed));
        Assert.Equal("The release list couldn't be read.", e.Message);
    }

    [Fact]
    public void A_local_list_may_name_files_relative_to_itself_and_a_web_list_may_not_name_local_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), "feed");
        var local = new Uri(Path.Combine(folder, "releases.json"));
        var fileUrl = new Uri(Path.Combine(folder, "other.zip")).AbsoluteUri;
        string json = $$"""
            [ { "tag_name": "v0.2.0", "assets": [
                { "name": "a.zip", "size": 1, "browser_download_url": "a.zip" },
                { "name": "b.zip", "size": 1, "browser_download_url": "{{fileUrl}}" },
                { "name": "c.zip", "size": 1, "browser_download_url": "http://example.com/c.zip" },
                { "name": "d.zip", "size": 1, "browser_download_url": "" } ] } ]
            """;

        var fromDisk = Assert.Single(ReleaseFeed.Parse(json, local));
        Assert.Equal(new Uri(Path.Combine(folder, "a.zip")), fromDisk.Asset("a.zip")!.DownloadUrl);
        Assert.Equal(new Uri(fileUrl), fromDisk.Asset("b.zip")!.DownloadUrl);
        Assert.Null(fromDisk.Asset("c.zip"));
        Assert.Null(fromDisk.Asset("d.zip"));

        var fromWeb = Assert.Single(ReleaseFeed.Parse(json, GitHubFeed));
        Assert.Equal(new Uri("https://api.github.com/repos/InspireNXE/Orrery/a.zip"), fromWeb.Asset("a.zip")!.DownloadUrl);
        Assert.Null(fromWeb.Asset("b.zip"));
        Assert.Null(fromWeb.Asset("c.zip"));
    }

    [Fact]
    public void Picks_the_newest_release_newer_than_this_one()
    {
        var newest = ReleaseFeed.Newest([R("0.1.1"), R("0.3.0"), R("0.2.0"), R("0.0.9")], Current, previews: false, null);
        Assert.Equal("v0.3.0", newest!.Tag);
    }

    [Fact]
    public void Nothing_newer_is_nothing_to_offer()
    {
        Assert.Null(ReleaseFeed.Newest([R("0.1.0"), R("0.0.9"), R("0.1.0-rc.1")], Current, previews: true, null));
        Assert.Null(ReleaseFeed.Newest([], Current, previews: true, null));
    }

    [Fact]
    public void Previews_only_when_asked_for()
    {
        Release[] releases = [R("0.2.0"), R("0.3.0-preview.1"), R("0.2.1", prerelease: true)];

        Assert.Equal("v0.2.0", ReleaseFeed.Newest(releases, Current, previews: false, null)!.Tag);
        Assert.Equal("v0.3.0-preview.1", ReleaseFeed.Newest(releases, Current, previews: true, null)!.Tag);
    }

    [Fact]
    public void A_preview_is_offered_the_next_preview_unless_previews_were_turned_off()
    {
        var preview = ReleaseVersion.Parse("0.2.0-preview.1");
        Release[] releases = [R("0.2.0-preview.1"), R("0.2.0-preview.2"), R("0.1.9")];

        Assert.True(new AppSettings().OffersPreviewUpdatesTo(preview));
        Assert.False(new AppSettings().OffersPreviewUpdatesTo(Current));
        Assert.False(new AppSettings { UpdatePreviews = false }.OffersPreviewUpdatesTo(preview));
        Assert.True(new AppSettings { UpdatePreviews = true }.OffersPreviewUpdatesTo(Current));

        var offered = ReleaseFeed.Newest(releases, preview, new AppSettings().OffersPreviewUpdatesTo(preview), null);
        Assert.Equal("v0.2.0-preview.2", offered!.Tag);
        Assert.Null(ReleaseFeed.Newest(releases, preview, new AppSettings { UpdatePreviews = false }.OffersPreviewUpdatesTo(preview), null));
        Assert.Equal("v0.2.0", ReleaseFeed.Newest([.. releases, R("0.2.0")], preview, previews: false, null)!.Tag);
    }

    [Fact]
    public void A_skipped_release_and_anything_older_arent_offered_but_a_later_one_is()
    {
        Release[] releases = [R("0.1.5"), R("0.2.0")];

        Assert.Null(ReleaseFeed.Newest(releases, Current, previews: false, "0.2.0"));
        Assert.Null(ReleaseFeed.Newest(releases, Current, previews: false, "v0.2.0"));
        Assert.Equal("v0.2.1", ReleaseFeed.Newest([.. releases, R("0.2.1")], Current, previews: false, "0.2.0")!.Tag);
        Assert.Equal("v0.2.0", ReleaseFeed.Newest(releases, Current, previews: false, "not a version")!.Tag);
    }

    [Fact]
    public void The_feed_is_github_unless_the_variable_points_elsewhere()
    {
        Assert.Equal(ReleaseFeed.GitHub, ReleaseFeed.Location(null));
        Assert.Equal(ReleaseFeed.GitHub, ReleaseFeed.Location("  "));
        Assert.Equal(new Uri("http://localhost:8000/releases.json"), ReleaseFeed.Location("http://localhost:8000/releases.json"));

        var path = Path.Combine(Path.GetTempPath(), "test-release", "releases.json");
        Assert.Equal(new Uri(path), ReleaseFeed.Location(path));
        Assert.True(ReleaseFeed.Location(new Uri(path).AbsoluteUri).IsFile);
    }
}
