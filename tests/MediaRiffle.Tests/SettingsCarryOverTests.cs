using System.Text.Json.Nodes;
using MediaRiffle.Server.Options;

namespace MediaRiffle.Tests;

public class SettingsCarryOverTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("mr-settings").FullName;
    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Write(string name, string json) { var p = Path.Combine(dir, name); File.WriteAllText(p, json); return p; }

    // The old program folder's file as shipped up to 0.5.1, with an admin's edits in it
    private const string OldProgramFolder = """
        {
          "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
          "AllowedHosts": "*",
          "MediaRiffle": {
            "DataDirectory": "",
            "ScanIntervalMinutes": 60,
            "TrustedProxies": [ "127.0.0.1", "::1", "192.168.0.1", "192.168.0.2", "192.168.0.3" ],
            "ForwardLimit": 1,
            "Libraries": [],
            "Google": { "ClientId": "abc.apps.googleusercontent.com", "ClientSecret": "s3cret" },
            "Transcoding": { "FfmpegFolder": "", "Encoder": "Libx264", "MaxHeight": 1080, "MaxVideoKbps": 8000, "SegmentSeconds": 4 }
          }
        }
        """;

    // The new program folder's file: no MediaRiffle section
    private const string NewProgramFolder = """
        { "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } }, "AllowedHosts": "*" }
        """;

    // The settings file as its template wrote it (comments and all), with the listening address changed
    private const string SystemTemplate = """
        {
          // MediaRiffle settings
          "MediaRiffle": {
            "TrustedProxies": [ "127.0.0.1", "::1" ],
            "ForwardLimit": 1,
            "Transcoding": { "Encoder": "Libx264" }
          },
          "Kestrel": { "Endpoints": { "Http": { "Url": "http://192.168.0.30:5080" } } }
        }
        """;

    [Fact]
    public void TheProgramFoldersOwnSettingsAreCarriedAndDefaultsArent()
    {
        var old = Write("old.json", OldProgramFolder);
        var current = Write("program.json", NewProgramFolder);
        var settings = Write("appsettings.json", SystemTemplate);

        Assert.Equal(0, SettingsCarryOver.Run(old, current, settings));

        var s = SettingsCarryOver.Parse(File.ReadAllText(settings));
        var mr = s["MediaRiffle"]!;
        // The template's default proxies give way to the admin's own
        Assert.Equal(5, mr["TrustedProxies"]!.AsArray().Count);
        Assert.Equal("abc.apps.googleusercontent.com", (string?)mr["Google"]!["ClientId"]);
        Assert.Equal("s3cret", (string?)mr["Google"]!["ClientSecret"]);
        // Defaults aren't copied in, and the settings file's own setting stays
        Assert.Null(mr["Transcoding"]!["MaxHeight"]);
        Assert.Null(mr["ScanIntervalMinutes"]);
        Assert.Equal("http://192.168.0.30:5080", (string?)s["Kestrel"]!["Endpoints"]!["Http"]!["Url"]);
        // The original, comments and all, is kept beside it
        Assert.Single(Directory.GetFiles(dir, "appsettings.json.*.bak"));
    }

    [Fact]
    public void ASettingTheSettingsFileChangedItselfWins()
    {
        var old = Write("old.json", OldProgramFolder);
        var current = Write("program.json", NewProgramFolder);
        var settings = Write("appsettings.json", """{ "MediaRiffle": { "TrustedProxies": [ "10.0.0.1" ] } }""");

        SettingsCarryOver.Run(old, current, settings);

        var proxies = SettingsCarryOver.Parse(File.ReadAllText(settings))["MediaRiffle"]!["TrustedProxies"]!.AsArray();
        Assert.Equal(["10.0.0.1"], proxies.Select(p => (string?)p));
    }

    [Fact]
    public void NoSettingsFileYetIsMadeFromTheCarriedOnes()
    {
        var old = Write("old.json", OldProgramFolder);
        var current = Write("program.json", NewProgramFolder);
        var settings = Path.Combine(dir, "sub", "appsettings.json");

        SettingsCarryOver.Run(old, current, settings);

        var s = SettingsCarryOver.Parse(File.ReadAllText(settings));
        Assert.Equal("s3cret", (string?)s["MediaRiffle"]!["Google"]!["ClientSecret"]);
    }

    [Fact]
    public void AnUneditedOldFileChangesNothing()
    {
        var untouched = OldProgramFolder
            .Replace("""[ "127.0.0.1", "::1", "192.168.0.1", "192.168.0.2", "192.168.0.3" ]""", """[ "127.0.0.1", "::1" ]""")
            .Replace("abc.apps.googleusercontent.com", "").Replace("s3cret", "");
        var old = Write("old.json", untouched);
        var current = Write("program.json", NewProgramFolder);
        var settings = Write("appsettings.json", SystemTemplate);

        SettingsCarryOver.Run(old, current, settings);

        Assert.Equal(SystemTemplate, File.ReadAllText(settings));
        Assert.Empty(Directory.GetFiles(dir, "*.bak"));
    }

    [Fact]
    public void TheLogNamesSettingsLeftInTheProgramFolder()
    {
        Assert.Empty(SettingsCarryOver.OwnSettings(Write("program.json", NewProgramFolder)));
        var own = SettingsCarryOver.OwnSettings(Write("old.json", OldProgramFolder));
        Assert.Contains("MediaRiffle:Google:ClientId", own);
        Assert.Contains("MediaRiffle:TrustedProxies", own);
        Assert.DoesNotContain("MediaRiffle:ScanIntervalMinutes", own);
    }
}
