# Installing on Windows Server 2025

There are two ways: as a **Windows service** with the installer (simplest), or **inside IIS**. Either way a reverse
proxy (HAProxy, or IIS itself) handles HTTPS. Names are placeholders: `EXAMPLE\gmsa-mediariffle$`, `MEDIA01`,
`files.example.lan`, `media.example.com`.

## As a Windows service (installer)

1. Install ffmpeg: a Windows build (e.g. from https://www.gyan.dev/ffmpeg/builds/), unpacked so that
   `C:\Tools\ffmpeg\bin\ffmpeg.exe` exists.
2. Run `MediaRiffle-v<version>-win-x64-setup.exe`. It needs no .NET runtime (it carries its own), and it:
   - installs to `C:\Program Files\MediaRiffle`;
   - creates the **MediaRiffle** service (automatic, delayed start, restarted if it stops) running as its own virtual
     account, `NT SERVICE\MediaRiffle`, which may change only `C:\ProgramData\MediaRiffle`;
   - writes `C:\ProgramData\MediaRiffle\appsettings.json` if there isn't one, and starts the service;
   - optionally opens TCP 5080 in Windows Firewall (off by default; only needed when the proxy is on another
     machine).
3. Edit `C:\ProgramData\MediaRiffle\appsettings.json` (the ffmpeg folder, the listening address, the proxy's address;
   see [configuration.md](configuration.md)), then `Restart-Service MediaRiffle`.
4. Open `http://localhost:5080/` on the server, or the site through the proxy. The first visit creates the admin
   account.

**Reading a file share:** on the network the virtual account acts as the computer itself (`EXAMPLE\MEDIA01$`). Add
the computer account to the `MediaRiffle Readers` group ([file-server.md](file-server.md)), restart the server so
the new group membership applies, and UNC library paths work with no password to keep. To run the service as a gMSA
instead: `sc.exe config MediaRiffle obj= "EXAMPLE\gmsa-mediariffle$"` (then grant that account Modify on
`C:\ProgramData\MediaRiffle`).

**Upgrading:** run the newer installer; it stops the service, replaces the program and starts it again. Settings and
data are kept. **Uninstalling** (Apps & Features) removes the service and the program; `C:\ProgramData\MediaRiffle`
stays, for a reinstall.

## Inside IIS

MediaRiffle runs inside IIS through the ASP.NET Core Module, in its own app pool, under an identity that can read the
media share (see [file-server.md](file-server.md)). Use the release's `MediaRiffle-v<version>-win-x64.zip` instead of
building it, unpacked to `C:\inetpub\mediariffle`.

## 1. Prerequisites

1. IIS with these role services: *Web Server → Common HTTP Features*, *Application Development → Application
   Initialization* (keeps the app running so scans happen), *Security → Request Filtering*.
2. The **ASP.NET Core 10 Hosting Bundle** from https://dotnet.microsoft.com/download/dotnet/10.0 (installs the
   runtime and the ASP.NET Core Module). Run `iisreset` afterwards.
3. ffmpeg: a Windows build (e.g. from https://www.gyan.dev/ffmpeg/builds/ or https://github.com/BtbN/FFmpeg-Builds),
   unpacked to `C:\Tools\ffmpeg` so that `C:\Tools\ffmpeg\bin\ffmpeg.exe` exists.

## 2. Build and copy the program

```powershell
dotnet publish src\MediaRiffle.Server -c Release -r win-x64 --self-contained false -o publish\win-x64
```

Copy `publish\win-x64\` to `C:\inetpub\mediariffle\`. The publish includes a `web.config` for the ASP.NET Core Module.

## 3. App pool and site

```powershell
Import-Module WebAdministration
New-WebAppPool -Name "MediaRiffle"
Set-ItemProperty IIS:\AppPools\MediaRiffle -Name managedRuntimeVersion -Value ""          # No Managed Code
Set-ItemProperty IIS:\AppPools\MediaRiffle -Name startMode -Value AlwaysRunning
Set-ItemProperty IIS:\AppPools\MediaRiffle -Name processModel.idleTimeout -Value "00:00:00"
Set-ItemProperty IIS:\AppPools\MediaRiffle -Name recycling.periodicRestart.time -Value "00:00:00"

# Run as the gMSA (no password; Windows manages it)
Set-ItemProperty IIS:\AppPools\MediaRiffle -Name processModel -Value @{ userName = "EXAMPLE\gmsa-mediariffle$"; identityType = 3 }
# Or a plain domain account:
# Set-ItemProperty IIS:\AppPools\MediaRiffle -Name processModel -Value @{ userName = "EXAMPLE\svc-mediariffle"; password = "…"; identityType = 3 }

New-Website -Name "MediaRiffle" -PhysicalPath "C:\inetpub\mediariffle" -ApplicationPool "MediaRiffle" `
  -HostHeader "media.example.com" -Port 443 -Ssl
Set-ItemProperty "IIS:\Sites\MediaRiffle" -Name applicationDefaults.preloadEnabled -Value $true
```

Bind your certificate to the site (IIS Manager → the site → *Bindings* → https → choose the certificate). If HAProxy
terminates HTTPS in front of IIS, bind plain HTTP on the LAN address instead and see
[reverse-proxy.md](reverse-proxy.md).

## 4. Folder permissions

```powershell
$id = "EXAMPLE\gmsa-mediariffle$"
New-Item -ItemType Directory -Force C:\ProgramData\MediaRiffle | Out-Null
icacls C:\ProgramData\MediaRiffle /grant "${id}:(OI)(CI)M"       # database, keys, pictures, transcodes
icacls C:\inetpub\mediariffle /grant "${id}:(OI)(CI)RX"          # the program, read-only
icacls C:\Tools\ffmpeg /grant "${id}:(OI)(CI)RX"
```

The media share's permissions were set on the file server (`MediaRiffle Readers`).

## 5. Settings

`C:\ProgramData\MediaRiffle\appsettings.json`:

```json
{
  "MediaRiffle": {
    "TrustedProxies": [ "10.0.0.5" ],
    "Libraries": [
      { "Name": "TV Shows", "Kind": "Shows",  "Path": "\\\\files.example.lan\\Media\\Shows" },
      { "Name": "Movies",   "Kind": "Movies", "Path": "\\\\files.example.lan\\Media\\Movies" }
    ],
    "Transcoding": {
      "FfmpegFolder": "C:\\Tools\\ffmpeg\\bin",
      "Encoder": "Libx264"
    }
  }
}
```

- `Libraries` only adds the first libraries on the first start; afterwards add and change them under
  **Admin → Libraries** (UNC paths work there too).
- `TrustedProxies` lists HAProxy's address, if HAProxy is in front of IIS. Remove it if browsers connect to IIS
  directly.
- `Nvenc` (NVIDIA) and `Qsv` (Intel) encoders work on Windows; `Vaapi` is Linux only.

Restart the app pool (`Restart-WebAppPool MediaRiffle`) and open `https://media.example.com/`. The first visit creates
the admin account.

## Troubleshooting

- **The page shows HTTP 500.30**: the app didn't start. Set `stdoutLogEnabled="true"` in `web.config` and look in
  `C:\inetpub\mediariffle\logs`, or check the *Application* event log (source *IIS AspNetCore Module V2*).
- **Libraries are empty**: the app pool identity can't read the UNC path. Test with
  `Invoke-Command -ComputerName localhost -ScriptBlock { Get-ChildItem \\files.example.lan\Media } -Credential …`, or
  check the scan result under **Admin**.
- **ffmpeg not found**: check `FfmpegFolder`, and that the identity can read it.
