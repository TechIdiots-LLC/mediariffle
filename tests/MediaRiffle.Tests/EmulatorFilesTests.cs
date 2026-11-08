using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using MediaRiffle.Server.Games;
using MediaRiffle.Server.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaRiffle.Tests;

public class EmulatorFilesTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory();
    private readonly EmulatorFiles emulator;

    public EmulatorFilesTests() =>
        emulator = new EmulatorFiles(Microsoft.Extensions.Options.Options.Create(new MediaRiffleOptions { DataDirectory = dir.FullName }),
            NullLogger<EmulatorFiles>.Instance);

    public void Dispose() => dir.Delete(true);

    /// <summary>An npm .tgz as the registry has them: everything in a package/ folder.</summary>
    private static MemoryStream Package(params (string Path, string Content)[] files)
    {
        var tgz = new MemoryStream();
        using (var gzip = new GZipStream(tgz, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
            foreach (var (path, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        tgz.Position = 0;
        return tgz;
    }

    private static readonly string[] Scripts =
        ["emulator.js", "nipplejs.js", "shaders.js", "storage.js", "gamepad.js", "GameManager.js", "socket.io.min.js", "compression.js"];

    private static (string, string)[] MainPackage() =>
    [
        ("package/package.json", """{ "name": "@emulatorjs/emulatorjs", "version": "4.2.3" }"""),
        ("package/data/loader.js", "// loader"),
        ("package/data/version.json", """{ "version": "4.2.3" }"""),
        ("package/data/emulator.css", ".ejs {}"),
        .. Scripts.Select(s => ($"package/data/src/{s}", $"// {s}")),
    ];

    [Fact]
    public async Task EmulatorJsIsPutInPlaceWithTheBundleItsLoaderLooksFor()
    {
        Assert.Null(emulator.Installed);
        Assert.Equal("Added EmulatorJS 4.2.3.", await emulator.AddUploadAsync(Package(MainPackage()), default));
        Assert.Equal("4.2.3", emulator.Installed);
        var bundle = File.ReadAllText(Path.Combine(emulator.DataDirectory, "emulator.min.js"));
        // Every script, in the loader's order
        Assert.Equal(Scripts.Select(s => $"// {s}"), Scripts.Select(s => bundle.Substring(bundle.IndexOf($"// {s}", StringComparison.Ordinal), s.Length + 3)));
        Assert.True(bundle.IndexOf("// emulator.js", StringComparison.Ordinal) < bundle.IndexOf("// compression.js", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(emulator.DataDirectory, "emulator.min.css")));
    }

    [Fact]
    public async Task ACoreAddsItsBuildsAndReport_AndKeepsThemWhenEmulatorJsIsAddedAgain()
    {
        await emulator.AddUploadAsync(Package(
            ("package/package.json", """{ "name": "@emulatorjs/core-snes9x", "version": "4.2.3" }"""),
            ("package/snes9x-wasm.data", "core"), ("package/snes9x-legacy-wasm.data", "core"),
            ("package/reports/snes9x.json", """{ "core": "snes9x" }"""), ("package/README.md", "x")), default);
        Assert.Equal(["snes9x"], emulator.Cores());
        await emulator.AddUploadAsync(Package(MainPackage()), default);
        Assert.Equal(["snes9x"], emulator.Cores());
    }

    [Fact]
    public async Task OnlyEmulatorJsAndItsCoresAreTaken()
    {
        var ex = await Assert.ThrowsAsync<PackageException>(() => emulator.AddUploadAsync(Package(
            ("package/package.json", """{ "name": "left-pad", "version": "1.0.0" }""")), default));
        Assert.Contains("isn't EmulatorJS", ex.Message);
    }

    [Fact]
    public async Task AFileOutsideThePackageFolderIsRefused()
    {
        await Assert.ThrowsAsync<PackageException>(() => emulator.AddUploadAsync(Package(
            ("package/package.json", """{ "name": "@emulatorjs/core-x", "version": "1" }"""),
            ("package/../../evil.js", "x")), default));
        Assert.False(File.Exists(Path.Combine(dir.FullName, "evil.js")));
    }

    [Theory]
    [InlineData("scph5501.bin", "scph5501.bin")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData(".hidden", null)]
    [InlineData("bios.bin.part", null)]
    [InlineData("", null)]
    public void BiosNamesArePlainFileNames(string given, string? kept) => Assert.Equal(kept, EmulatorFiles.SafeName(given));

    [Fact]
    public async Task BiosFilesAreKeptPerSystem()
    {
        Assert.Null(await emulator.AddBiosAsync("psx", "scph5501.bin", new MemoryStream([1, 2, 3]), default));
        Assert.Equal(["scph5501.bin"], emulator.Bios("psx"));
        Assert.NotNull(await emulator.AddBiosAsync("nosuchsystem", "x.bin", new MemoryStream([1]), default));
        Assert.True(emulator.RemoveBios("psx", "scph5501.bin"));
        Assert.Empty(emulator.Bios("psx"));
    }
}
