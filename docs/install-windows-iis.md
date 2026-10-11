# Installing on Windows Server 2025 with IIS

MediaRiffle runs inside IIS through the ASP.NET Core Module, in its own app pool, under an identity that can read the
media share (see [file-server.md](file-server.md)). Names are placeholders: `EXAMPLE\gmsa-mediariffle$`,
`files.example.lan`, `media.example.com`.

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
