# MediaRiffle

Stream your own video library to a browser or a Chromecast. MediaRiffle scans folders of TV shows and movies, and
plays each file the cheapest way the viewer's browser allows:

- **Direct**: files the browser already plays (MP4 with H.264/AAC, WebM) are sent as they are, with normal seeking.
- **Remux**: H.264 video in other containers (MKV, AVI, …) is copied into an HLS stream; only the audio is converted
  when it has to be. Very little CPU.
- **Transcode**: anything else (HEVC, 10-bit, MPEG-4, …) is converted to H.264 on the fly, scaled down to the
  configured maximum, with image subtitles burned in when picked. Hardware encoding (NVENC, Quick Sync, VA-API) is a
  setting.

Seeking works anywhere, in all three modes: inside what a stream has already made, or by starting a new stream at that
point. Each user's position is saved, so they can resume where they stopped.

It's the successor of the 2014 *Tongue* project (Python daemon + ffserver + PHP), which is no longer workable:
ffserver was removed from FFmpeg in 2018 and the player used Flash.

## Features

- TV show (Show / Season NN / episode) and movie libraries, including movies split into cd1/cd2 parts, managed by
  admins in the browser (local folders, mounts or file shares)
- Tags per library, as in Media Library Manager, which become categories to browse, play through or shuffle
- Quality: Auto (adaptive streaming: the player moves between levels segment by segment as the connection allows),
  Original, 1080p, 720p, 480p, 360p; x265 sent as it is to devices that decode HEVC; per-user quality caps
- Audio track choice; subtitles from the file or side-car `.srt`/`.vtt`/`.ass` files, shown as WebVTT
- Pictures: `poster.jpg`/`folder.jpg` beside the media when present, otherwise a frame from the video
- Continue watching, next episode
- Chromecast (Default Media Receiver)
- Accounts by invitation only; admins and viewers

## Security

- No open sign-up: the first visit creates the admin, everyone else joins through a one-time invite link
- Passwords hashed by ASP.NET Core Identity; account lockout after 5 failures; sign-in rate limited per address
- Cookies `HttpOnly`, `Secure`, `SameSite=Strict`, plus an antiforgery token on every change
- Strict Content Security Policy, no inline script; nothing is ever built from HTML strings
- ffmpeg is started with an argument list, never a shell, so a file name can't be read as a command
- Files are only read from inside the configured library folders
- Streams are reached through an unguessable, short-lived session URL (that's what lets a Chromecast play them)

## Running it

| Platform | Guide |
| --- | --- |
| Ubuntu 24.04 (.deb) and other systemd Linux | [docs/install-ubuntu.md](docs/install-ubuntu.md) |
| Windows Server 2025 (service installer or IIS) | [docs/install-windows-iis.md](docs/install-windows-iis.md) |
| Media on another server (SMB, domain accounts) | [docs/file-server.md](docs/file-server.md) |
| HAProxy, Apache or nginx in front | [docs/reverse-proxy.md](docs/reverse-proxy.md) |

All settings are described in [docs/configuration.md](docs/configuration.md).

## Developing

Needs the .NET 10 SDK and ffmpeg/ffprobe (on `PATH`, or set `MediaRiffle:Transcoding:FfmpegFolder`).
Open `MediaRiffle.slnx` in Visual Studio 2026 (or 2022 17.13+), or:

```sh
dotnet test
dotnet run --project src/MediaRiffle.Server
```

In Development, settings can go in `src/MediaRiffle.Server/appsettings.Development.json`. Database changes are EF Core
migrations: `dotnet tool restore`, then `dotnet ef migrations add <Name> -p src/MediaRiffle.Server -o Data/Migrations`.

The web UI is plain JavaScript modules in `src/MediaRiffle.Server/wwwroot` with no build step;
[hls.js](https://github.com/video-dev/hls.js) (Apache-2.0) is in `wwwroot/lib`.

## Releases

Each release has, for x64 and arm64: a Windows service installer (`-setup.exe`), a portable Windows zip (also for
IIS), a `.deb` for Ubuntu/Debian, and a `.tar.gz` with `install.sh` for other Linux distributions.

The version is `<Version>` in `src/MediaRiffle.Server/MediaRiffle.Server.csproj`. When a push to `main` carries a
version with no release yet, the pipeline builds every package (`build/Package.ps1`) and publishes the release with
that version's `CHANGELOG.md` section as the notes. To make one:

- **GitLab:** Build → Pipelines → New pipeline on `main`, with `BUMP_VERSION` set (`patch`, `minor`, …). It opens a
  `release-v<version>` merge request; merging it releases.
- **GitHub:** Actions → *Create bump version PR* → Run workflow; merging the pull request releases.

New changes go under `## master` in `CHANGELOG.md`; the bump moves them into the new version.

Packages can also be built by hand: `./build/Package.ps1 -Rid win-x64` (needs NSIS for the installer) or
`-Rid linux-x64` (needs only the .NET SDK).
