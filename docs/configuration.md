# Configuration

MediaRiffle reads, in order (later wins):

1. `appsettings.json` next to the program (defaults; replaced on every upgrade, don't edit it there)
2. `/etc/mediariffle/appsettings.json` on Linux, `%ProgramData%\MediaRiffle\appsettings.json` on Windows
3. Environment variables, with `__` for nesting: `MediaRiffle__Transcoding__Encoder=Vaapi`

Put your settings in file 2. A complete example:

```json
{
  "MediaRiffle": {
    "DataDirectory": "",
    "ScanIntervalMinutes": 60,
    "TrustedProxies": [ "127.0.0.1" ],
    "ForwardLimit": 1,
    "Libraries": [
      { "Name": "TV Shows", "Kind": "Shows",  "Path": "/mnt/media/Shows" },
      { "Name": "Movies",   "Kind": "Movies", "Path": "/mnt/media/Movies" }
    ],
    "Transcoding": {
      "FfmpegFolder": "",
      "Encoder": "Libx264",
      "VaapiDevice": "/dev/dri/renderD128",
      "MaxHeight": 1080,
      "MaxVideoKbps": 8000,
      "MaxSessions": 4,
      "MaxSessionsPerUser": 2,
      "SegmentSeconds": 4,
      "IdleTimeoutSeconds": 90
    }
  },
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://127.0.0.1:5080" } }
  }
}
```

| Setting | Meaning |
| --- | --- |
| `DataDirectory` | Database, sign-in keys, cached pictures and running transcodes. Empty means `/var/lib/mediariffle` (Linux) or `%ProgramData%\MediaRiffle` (Windows). The service account needs read/write here and nowhere else. |
| `ScanIntervalMinutes` | How often the libraries are rescanned; 0 for only at start and on Admin → Scan now. |
| `TrustedProxies` | IP addresses of the reverse proxies in front (HAProxy, Apache, nginx). Only these may set `X-Forwarded-For`/`-Proto`. |
| `ForwardLimit` | How many proxies are in the chain: 1 for HAProxy alone, 2 for HAProxy → Apache. |
| `Libraries[].Name` | Shown in the UI; unique. |
| `Libraries[].Kind` | `Shows` (`Show/Season 01/Show - S01E01 - Title.mkv`) or `Movies` (`Movie (1999).mkv`, `Movie/cd1.avi`). |
| `Libraries[].Path` | The folder: a local path, a mount point (`/mnt/media/Shows`), or on Windows a UNC path (`\\files.example.lan\Media\Shows`). Only files under it are read. |
| `Transcoding.FfmpegFolder` | Folder holding `ffmpeg` and `ffprobe`; empty to use `PATH`. |
| `Transcoding.Encoder` | `Libx264` (CPU), `Nvenc` (NVIDIA), `Qsv` (Intel Quick Sync), `Vaapi` (Intel/AMD on Linux). |
| `Transcoding.MaxHeight` | Transcodes are scaled down to this many lines. |
| `Transcoding.MaxVideoKbps` | Peak video bitrate of a transcode. |
| `Transcoding.MaxSessions` | Remuxes and transcodes running at once, all users together. Direct play doesn't count. |
| `Transcoding.MaxSessionsPerUser` | A user starting more stops their oldest. |
| `Transcoding.IdleTimeoutSeconds` | A stream nobody fetches from for this long is stopped and its files deleted. |
| `Kestrel:Endpoints` | Where MediaRiffle listens. `127.0.0.1` when the proxy is on the same machine; the machine's LAN address when it isn't (then firewall the port to the proxy only). Not used under IIS. |

## Quality

The player has a quality menu: **Auto**, **Original**, and 1080p (8 Mbit/s), 720p (4), 480p (1.5), 360p (0.8).
Only one stream runs per viewer; changing quality restarts it at the same point in the video.

- **Original** sends the file's own video when the device can decode it: H.264 everywhere, and HEVC (x265) on
  devices that report it (Safari; Chrome/Edge with a hardware HEVC decoder; Chrome on Android). Copied HEVC goes out
  as fMP4 segments. Anything the device can't decode is transcoded to H.264 at the server's maximum
  (`MaxHeight`/`MaxVideoKbps`).
- **A level** transcodes to that height and bitrate, unless the file already fits in it (then it's sent as it is).
- **Auto** starts at the best level the measured connection carries with room to spare, steps down after repeated
  stalls or when downloads fall behind, and steps back up after a minute with plenty of headroom.
- Casting always uses H.264, as most Chromecasts can't decode HEVC.

Admins can cap a user's quality (**Admin → Users → Best quality**), e.g. 720p for someone streaming over a slow
connection, so one viewer can't use all of the server's upload. A capped user doesn't get Original.

## Library layout

```
Shows/
  The Simpsons/
    poster.jpg                         (optional show art)
    Season 01/
      The Simpsons - S01E01 - Simpsons Roasting on an Open Fire.mkv
      The Simpsons - S01E01 - Simpsons Roasting on an Open Fire.en.srt
    Specials/
      The Simpsons - S00E01 - Something.mkv
Movies/
  HD/The Matrix (1999).mkv
  HD/The Matrix (1999).jpg             (optional art named after the file)
  SD/Fargo/cd1.avi
  SD/Fargo/cd2.avi
```

`S01E02` and `1x02` episode numbers are both understood; a `Season NN` folder sets the season when the name has none.
Release tags (`1080p`, `x264`, `BluRay`, …) are dropped from titles.
