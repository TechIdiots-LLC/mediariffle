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
| `Libraries` | Only used on the **first start**, to add the first libraries. After that they're managed under **Admin → Libraries** (see below), and this setting is ignored. |
| `Libraries[].Kind` | `Shows` (`Show/Season 01/Show - S01E01 - Title.mkv`) or `Movies` (`Movie (1999).mkv`, `Movie/cd1.avi`). |
| `Libraries[].Path` | The folder: a local path, a mount point (`/mnt/media/Shows`), or on Windows a UNC path (`\\files.example.lan\Media\Shows`). Only files under it are read. |
| `Transcoding.FfmpegFolder` | Folder holding `ffmpeg` and `ffprobe`; empty to use `PATH`. |
| `Transcoding.Encoder` | `Libx264` (CPU), `Nvenc` (NVIDIA), `Qsv` (Intel Quick Sync), `Vaapi` (Intel/AMD on Linux). |
| `Transcoding.MaxHeight` | Transcodes are scaled down to this many lines. |
| `Transcoding.MaxVideoKbps` | Peak video bitrate of a transcode. |
| `Transcoding.MaxSessions` | Remuxes and transcodes running at once, all users together. Direct play doesn't count. |
| `Transcoding.MaxSessionsPerUser` | A user starting more stops their oldest. |
| `Transcoding.SegmentSeconds` | Length of a stream segment. Shorter switches level and starts sooner; longer is a little more efficient. |
| `Transcoding.IdleTimeoutSeconds` | A stream nobody fetches from for this long is stopped and its files deleted (a paused player keeps its stream). |
| `Kestrel:Endpoints` | Where MediaRiffle listens. `127.0.0.1` when the proxy is on the same machine; the machine's LAN address when it isn't (then firewall the port to the proxy only). Not used under IIS. |

## Libraries and categories

Admins add, edit and remove libraries under **Admin → Libraries**: a name, whether it holds shows or movies, its folder,
and tags. **Check folder** shows whether this server's account can read the folder and a few of the videos it found,
before anything is saved. A new library, or a changed folder, is scanned straight away.

Tags work as in Media Library Manager: a comma-separated list on each library (`Cartoons, Kids`), applying to
everything in it. Every tag becomes a **category** on the home page, gathering the shows and movies of all the
libraries carrying it, with **Play all** (show by show, in season and episode order) and **Shuffle** (every episode and
movie mixed together). Libraries and shows have the same two buttons. A playing queue moves on to the next item by
itself, with a few seconds to cancel.

Disabling a library hides it without forgetting it; removing it forgets what was scanned (the files themselves are
never touched) and everyone's place in it.

## Quality

The player has a quality menu: **Auto**, **Original**, and 1080p (8 Mbit/s), 720p (4), 480p (1.5), 360p (0.8).

- **Auto** is an adaptive stream, as on streaming sites: a playlist offering every level (none above the picture's
  own size, the server's `MaxHeight` or the user's cap), from which the player picks a level segment by segment by
  timing each download, moving down as soon as the connection can't keep up and back up when it can. hls.js does
  this in Chrome, Edge and Firefox; Safari, Chromecasts and AirPlay receivers do it themselves.
  When the connection clearly has room for the file as it is, Auto sends that instead (no transcode at all), and
  moves to the adaptive stream if it stops keeping up.
- **Original** sends the file's own video when the device can decode it: H.264 everywhere, and HEVC (x265) on
  devices that report it (Safari; Chrome/Edge with a hardware HEVC decoder; Chrome on Android). Copied HEVC goes out
  as fMP4 segments. Anything the device can't decode is transcoded to H.264 at the best level allowed.
- **A level** transcodes to that height and bitrate, unless the file already fits in it (then it's sent as it is).
- Changing the menu restarts the stream at the same point in the video.
- Casting always uses H.264, as most Chromecasts can't decode HEVC.

The adaptive stream doesn't transcode every level at once. Its segments (`SegmentSeconds` long) are made when the
player asks for them: one ffmpeg per viewer makes the level being watched, from the point being watched, and is
replaced when the player switches level or seeks somewhere not made yet (a pause of about a second). Seeking within
what's made is instant, and an ffmpeg that gets three minutes ahead of the viewer (e.g. while paused) stops until
it's needed again. Seeking in a transcode never restarts the whole stream.

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
