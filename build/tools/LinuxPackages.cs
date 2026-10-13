// Builds MediaRiffle's Linux packages from a linux-x64/linux-arm64 publish folder, on any OS (no dpkg-deb or Unix tar
// needed): System.Formats.Tar writes the Unix modes and root ownership a Windows file system doesn't have.
//
//   dotnet run --file build/tools/LinuxPackages.cs -- <publish dir> <version> <amd64|arm64> <output .deb> <output .tar.gz>
//
// The .deb (an ar archive of debian-binary, control.tar.gz and data.tar.gz): /opt/mediariffle (the program),
// /lib/systemd/system/mediariffle.service, /etc/mediariffle/appsettings.json (a conffile, so upgrades keep the admin's
// edits); deploy/linux/debian/{postinst,prerm,postrm} create the service user and data folder and run the service.
//
// The .tar.gz, for other systemd distributions: <name>/app/ (the program), install.sh, mediariffle.service and a
// starting appsettings.json, where <name> is the file name without .tar.gz. Run from the repository root.
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

if (args.Length != 5) { Console.Error.WriteLine("usage: LinuxPackages <publish dir> <version> <amd64|arm64> <output .deb> <output .tar.gz>"); return 1; }
var (publish, version, arch, output, tarball) = (Path.GetFullPath(args[0]), args[1], args[2], Path.GetFullPath(args[3]), Path.GetFullPath(args[4]));
// Run from the repository root (as Package.ps1 does)
var deploy = Path.Combine(Environment.CurrentDirectory, "deploy", "linux");
if (!File.Exists(Path.Combine(deploy, "mediariffle.service"))) { Console.Error.WriteLine("Run from the repository root"); return 1; }
if (!File.Exists(Path.Combine(publish, "MediaRiffle.Server"))) { Console.Error.WriteLine($"No MediaRiffle.Server in {publish}"); return 1; }

// Debian versions can't hold '-' in the upstream part without a revision; 0.2.0-rc.1 sorts before 0.2.0 as 0.2.0~rc.1
var debVersion = version.Replace('-', '~') + "-1";
const UnixFileMode Dir = (UnixFileMode)0b111_101_101, Exe = (UnixFileMode)0b111_101_101, Plain = (UnixFileMode)0b110_100_100;
var mtime = DateTimeOffset.UtcNow;

byte[] Lf(string path) => Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n"));

byte[] TarGz(Action<TarWriter> fill)
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
    using (var tar = new TarWriter(gz, TarEntryFormat.Gnu, leaveOpen: true))
        fill(tar);
    return ms.ToArray();
}

void AddDir(TarWriter tar, string name) => tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./" + name.TrimEnd('/') + "/")
    { Mode = Dir, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = mtime });

void AddFile(TarWriter tar, string name, byte[] data, UnixFileMode mode)
{
    var e = new GnuTarEntry(TarEntryType.RegularFile, "./" + name)
        { Mode = mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = mtime, DataStream = new MemoryStream(data) };
    tar.WriteEntry(e);
}

long installedKb = 0;
var data = TarGz(tar =>
{
    tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./") { Mode = Dir, UserName = "root", GroupName = "root", ModificationTime = mtime });
    var made = new HashSet<string>();
    void Parents(string path)
    {
        var parts = path.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            var d = string.Join('/', parts[..i]);
            if (made.Add(d)) AddDir(tar, d);
        }
    }
    foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var rel = Path.GetRelativePath(publish, file).Replace('\\', '/');
        var name = "opt/mediariffle/" + rel;
        Parents(name);
        var bytes = File.ReadAllBytes(file);
        installedKb += (bytes.Length + 1023) / 1024;
        var exe = rel is "MediaRiffle.Server" or "createdump";
        AddFile(tar, name, bytes, exe ? Exe : Plain);
    }
    Parents("lib/systemd/system/mediariffle.service");
    AddFile(tar, "lib/systemd/system/mediariffle.service", Lf(Path.Combine(deploy, "mediariffle.service")), Plain);
    Parents("etc/mediariffle/appsettings.json");
    AddFile(tar, "etc/mediariffle/appsettings.json", Lf(Path.Combine(deploy, "appsettings.json")), Plain);
    // Where Debian keeps a package's license (it's also in /opt/mediariffle, from the publish folder)
    Parents("usr/share/doc/mediariffle/copyright");
    AddFile(tar, "usr/share/doc/mediariffle/copyright", Lf(Path.Combine(publish, "LICENSE")), Plain);
});

var controlText = $"""
    Package: mediariffle
    Version: {debVersion}
    Architecture: {arch}
    Maintainer: TechIdiots LLC <noreply@techidiots.net>
    Installed-Size: {installedKb}
    Depends: ffmpeg, libc6 (>= 2.35), libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, zlib1g, adduser
    Recommends: cifs-utils
    Section: video
    Priority: optional
    Homepage: https://gitlab.techidiots.net/techidiots-llc/mediariffle
    Description: Stream your video library to browsers and Chromecasts
     MediaRiffle scans folders of TV shows and movies and streams them to a web
     browser, playing each file as it is, remuxed or transcoded on the fly, with
     seeking, resume, subtitles, audio tracks, quality levels and invite-only
     accounts.

    """.Replace("\r\n", "\n");
var control = TarGz(tar =>
{
    AddFile(tar, "control", Encoding.UTF8.GetBytes(controlText), Plain);
    AddFile(tar, "conffiles", Encoding.UTF8.GetBytes("/etc/mediariffle/appsettings.json\n"), Plain);
    foreach (var script in new[] { "postinst", "prerm", "postrm" })
        AddFile(tar, script, Lf(Path.Combine(deploy, "debian", script)), Exe);
});

// ar archive: "!<arch>\n", then for each member a 60-byte header and its data, padded to an even length
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using (var deb = File.Create(output))
{
    void Member(string name, byte[] bytes)
    {
        var header = $"{name,-16}{mtime.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{bytes.Length,-10}`\n";
        deb.Write(Encoding.ASCII.GetBytes(header));
        deb.Write(bytes);
        if (bytes.Length % 2 == 1) deb.WriteByte((byte)'\n');
    }
    deb.Write("!<arch>\n"u8);
    Member("debian-binary", "2.0\n"u8.ToArray());
    Member("control.tar.gz", control);
    Member("data.tar.gz", data);
}
Console.WriteLine($"{output}: mediariffle {debVersion} ({arch}), {installedKb} KB installed");

// The tarball
var top = Path.GetFileName(tarball);
top = top.EndsWith(".tar.gz") ? top[..^".tar.gz".Length] : Path.GetFileNameWithoutExtension(top);
File.WriteAllBytes(tarball, TarGz(tar =>
{
    var made = new HashSet<string>();
    void Dirs(string path)
    {
        var parts = path.Split('/');
        for (int i = 1; i < parts.Length; i++)
        {
            var d = string.Join('/', parts[..i]);
            if (made.Add(d)) tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, d + "/")
                { Mode = Dir, UserName = "root", GroupName = "root", ModificationTime = mtime });
        }
    }
    void Put(string name, byte[] bytes, UnixFileMode mode)
    {
        Dirs(name);
        tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name)
            { Mode = mode, UserName = "root", GroupName = "root", ModificationTime = mtime, DataStream = new MemoryStream(bytes) });
    }
    foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var rel = Path.GetRelativePath(publish, file).Replace('\\', '/');
        Put($"{top}/app/{rel}", File.ReadAllBytes(file), rel is "MediaRiffle.Server" or "createdump" ? Exe : Plain);
    }
    Put($"{top}/install.sh", Lf(Path.Combine(deploy, "install.sh")), Exe);
    Put($"{top}/mediariffle.service", Lf(Path.Combine(deploy, "mediariffle.service")), Plain);
    Put($"{top}/appsettings.json", Lf(Path.Combine(deploy, "appsettings.json")), Plain);
}));
Console.WriteLine($"{tarball}: {top}/");
return 0;
