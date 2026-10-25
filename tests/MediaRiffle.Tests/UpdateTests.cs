using System.Runtime.InteropServices;
using MediaRiffle.Server.Updates;

namespace MediaRiffle.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("0.9.0", "0.8.0", 1)]
    [InlineData("0.10.0", "0.9.0", 1)]          // by number, not text
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]     // a pre-release before its release
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-rc.1", "0.9.9", 1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    public void VersionsCompareAsReleases(string a, string b, int expected) => Assert.Equal(expected, Math.Sign(VersionOrder.Compare(a, b)));

    [Fact]
    public void GitLabReleasesAreRead()
    {
        var list = Releases.Parse("""
            [ { "tag_name": "v0.9.0", "name": "MediaRiffle v0.9.0", "description": "### Features\n- more", "released_at": "2026-11-01T12:00:00Z",
                "assets": { "links": [ { "name": "mediariffle_0.9.0-1_amd64.deb" }, { "name": "SHA256SUMS" } ] } },
              { "tag_name": "v1.0.0-rc.1", "name": "rc", "description": "", "released_at": "2026-11-02T12:00:00Z", "assets": { "links": [] } },
              { "tag_name": "v2.0.0", "upcoming_release": true, "assets": { "links": [] } },
              { "tag_name": "nightly", "assets": { "links": [] } } ]
            """, UpdateSource.GitLab);
        Assert.Equal(["0.9.0", "1.0.0-rc.1"], list.Select(r => r.Version));
        Assert.Equal("### Features\n- more", list[0].Notes);
        Assert.Contains("SHA256SUMS", list[0].Files);
        Assert.False(list[0].Prerelease);
        Assert.True(list[1].Prerelease);
        Assert.Equal("https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/releases/v0.9.0", list[0].Url);
    }

    [Fact]
    public void GitHubReleasesAreRead()
    {
        var list = Releases.Parse("""
            [ { "tag_name": "v0.9.0", "name": "MediaRiffle v0.9.0", "body": "notes", "published_at": "2026-11-01T12:00:00Z", "html_url": "https://github.com/x",
                "prerelease": false, "draft": false, "assets": [ { "name": "MediaRiffle-v0.9.0-win-x64-setup.exe" }, { "name": "SHA256SUMS" } ] },
              { "tag_name": "v0.9.1", "draft": true, "assets": [] } ]
            """, UpdateSource.GitHub);
        Assert.Single(list);
        Assert.Equal("notes", list[0].Notes);
        Assert.Equal(["MediaRiffle-v0.9.0-win-x64-setup.exe", "SHA256SUMS"], list[0].Files);
    }

    [Theory]
    [InlineData("0.9.0", Installer.Deb, Architecture.X64, "mediariffle_0.9.0-1_amd64.deb")]
    [InlineData("1.0.0-rc.1", Installer.Deb, Architecture.Arm64, "mediariffle_1.0.0~rc.1-1_arm64.deb")]
    [InlineData("0.9.0", Installer.WindowsSetup, Architecture.X64, "MediaRiffle-v0.9.0-win-x64-setup.exe")]
    [InlineData("0.9.0", Installer.None, Architecture.X64, null)]
    public void TheFileThisInstallUpdatesFrom(string version, Installer installer, Architecture arch, string? expected) =>
        Assert.Equal(expected, Releases.FileFor(version, installer, arch));
}
