# Configuration

MediaRiffle reads, in order (later wins):

1. `appsettings.json` next to the program (logging only; replaced on every upgrade, so don't put settings there. If
   some are there anyway, the installers carry them into file 2 on the next upgrade, and the log warns at start)
2. `/etc/mediariffle/appsettings.json` on Linux, `%ProgramData%\MediaRiffle\appsettings.json` on Windows
3. Environment variables, with `__` for nesting: `MediaRiffle__Transcoding__Encoder=Vaapi`

Put your settings in file 2. A complete example:

```json
{
  "MediaRiffle": {
    "DataDirectory": "",
    "ScanIntervalMinutes": 60,
    "PrepareSubtitles": true,
    "SubtitleCacheGB": 5,
    "TrustedProxies": [ "127.0.0.1" ],
    "ForwardLimit": 1,
    "Google": { "ClientId": "", "ClientSecret": "" },
    "AdminMfa": { "Required": true, "Hours": 12 },
    "Libraries": [
      { "Name": "TV Shows", "Kind": "Shows",  "Path": "/mnt/media/Shows" },
      { "Name": "Movies",   "Kind": "Movies", "Path": "/mnt/media/Movies" }
    ],
    "Transcoding": {
      "FfmpegFolder": "",
      "Encoder": "Libx264",
      "VaapiDevice": "/dev/dri/renderD128",
      "MaxHeight": 1080,
      "ToneMapHdr": true,
      "UseLibplacebo": true,
      "HardwareDecoding": true,
      "MaxVideoKbps": 10000,
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
| `PrepareSubtitles` | Extract files' subtitle tracks ahead of time, in the background while nothing is streaming, so turning one on is instant (see Subtitles below). `true` by default. |
| `SubtitleCacheGB` | The most the kept subtitle tracks may take (the least recently used go first). 5 by default. |
| `Updates.Source` | Where Admin → Updates looks for new releases and downloads them from: `GitLab` (the default) or `GitHub`. See [updating](updating.md). |
| `Updates.Prereleases` | Offer pre-releases too. `false` by default. |
| `Updates.CheckHours` | How often to look for a new release; `0` only on **Check now**. 12 by default. |
| `TrustedProxies` | IP addresses of the reverse proxies in front (HAProxy, Apache, nginx). Only these may set `X-Forwarded-For`/`-Proto`. |
| `ForwardLimit` | How many proxies are in the chain: 1 for HAProxy alone, 2 for HAProxy → Apache. |
| `Libraries` | Only used on the **first start**, to add the first libraries. After that they're managed under **Admin → Libraries** (see below), and this setting is ignored. |
| `Libraries[].Kind` | `Shows` (`Show/Season 01/Show - S01E01 - Title.mkv`), `Movies` (`Movie (1999).mkv`, `Movie/cd1.avi`) or `Music` (`Artist/Album/01 - Title.mp3`, or by tags; see below). |
| `Libraries[].Path` | The folder: a local path, a mount point (`/mnt/media/Shows`), or on Windows a UNC path (`\\files.example.lan\Media\Shows`). Only files under it are read. |
| `Google.ClientId`, `Google.ClientSecret` | Turn on **Sign in with Google** (see below). Empty: off. |
| `AdminMfa.Required` | Admin asks for two-step verification (a passkey or an authenticator code), see below. Set `false` only to get back in when the only admin has lost every way to verify. |
| `AdminMfa.Hours` | How long a check lasts before Admin asks again. |
| `Transcoding.FfmpegFolder` | Folder holding `ffmpeg` and `ffprobe`; empty to use `PATH`. |
| `Transcoding.Encoder` | `Libx264` (CPU), `Nvenc` (NVIDIA), `Qsv` (Intel Quick Sync), `Vaapi` (Intel/AMD on Linux). |
| `Transcoding.MaxHeight` | Transcodes are scaled down to this many lines. `1440` adds a 1440p level, `2160` 1440p and 4K (see Quality below); 1080 by default. |
| `Transcoding.MaxVideoKbps` | Peak video bitrate of a transcode; 10000 by default (1080p's peak). Raise it to `16000` with `MaxHeight` 1440 and about `30000` with 2160, or those levels are held to this. At `14000` or more there is also **1080p High** (14 Mbit/s), a second 1080p level with more bits for a fast connection (a home network), which Auto uses when the connection carries it. |
| `Transcoding.ToneMapHdr` | HDR video (HDR10, HLG) that has to be converted is tone-mapped to ordinary colours, so it isn't washed out (see Quality below). `true` by default. |
| `Transcoding.HardwareDecoding` | With a GPU encoder (`Qsv`, `Vaapi`, `Nvenc`), decode on that GPU too and scale (and with `Vaapi`, tone-map HDR) there, for what it was found to decode at start (see Decoding on the GPU below). `true` by default. |
| `Transcoding.UseLibplacebo` | Tone-map HDR with libplacebo when this ffmpeg has it and a Vulkan GPU works: better HDR10, and Dolby Vision applied, which Dolby Vision 5 needs (see HDR below). `true` by default. |
| `Transcoding.MaxSessions` | Remuxes and transcodes running at once, all users together. Direct play doesn't count. |
| `Transcoding.MaxSessionsPerUser` | A user starting more stops their oldest. |
| `Transcoding.SegmentSeconds` | Length of a stream segment. Shorter switches level and starts sooner; longer is a little more efficient. |
| `Transcoding.IdleTimeoutSeconds` | A stream nobody fetches from for this long is stopped and its files deleted (a paused player keeps its stream). |
| `Kestrel:Endpoints` | Where MediaRiffle listens. `127.0.0.1` when the proxy is on the same machine; the machine's LAN address when it isn't (then firewall the port to the proxy only). Not used under IIS. |

## Sign in with Google

People can sign in with their Google account (OpenID Connect) once they've linked it to their MediaRiffle account.
Google never creates an account: people still join with an invite, then link Google under **their name → Profile →
Link Google account**. The same page shows which Google account is linked, and unlinks it. A Google account can be
linked to only one MediaRiffle account. Signing in with Google honours the same lockout as passwords.

To turn it on:

1. In the [Google Cloud console](https://console.cloud.google.com/apis/credentials), set up the OAuth consent screen
   (External; the `openid`, `email` and `profile` scopes are enough), then **Create credentials → OAuth client ID →
   Web application**.
2. Under **Authorized redirect URIs**, add `https://media.example.com/signin-google` (your address, ending in
   `/signin-google`). Google accepts `http://` only for `localhost`.
3. Put the client ID and secret in `MediaRiffle:Google` (or `MediaRiffle__Google__ClientId` /
   `MediaRiffle__Google__ClientSecret`), and restart MediaRiffle.

Behind a reverse proxy, the proxy must be in `TrustedProxies` and send `X-Forwarded-Proto` (and `X-Forwarded-Host`
if it changes the host name), or MediaRiffle sends Google an `http://` redirect address that doesn't match.

## Switch user

Several people can share one device (a family computer, a TV's browser) and switch between their accounts without
typing passwords, as with Google accounts or Netflix profiles: **your name → Switch user** lists the accounts signed
in on this device. The sign-in page shows them too ("Who's watching?"), with **Add another account** for someone new.
Each account keeps its own favorites, lists, hidden things and watch positions.

- Only sign-ins with **Keep me signed in** are listed, for 14 days after they were last used (as the sign-in itself
  lasts). An account whose password changed, or whose two-step verification an admin reset, asks for its password again.
- **Sign out** signs out the account in use and takes it off the device, then offers the others; **Sign out of all**
  clears the device. **×** takes one account off.
- An account's owner can set a **PIN** (4–8 digits) under **Profile → Switch user PIN**, so others on a shared device
  can't switch into it; setting or removing it asks for the password. Wrong PINs count toward the account's lockout.
- Switching to an admin doesn't open Admin: it still asks for its own two-step check.

The list is a cookie on the device, encrypted with the server's keys, holding the accounts' ids and security stamps.

## Two-step verification for Admin

Admin asks for a second step besides the password: a **passkey** (Face ID or Touch ID on an iPhone, iPad or Mac,
Windows Hello, an Android phone, or a security key) or a 6-digit code from an **authenticator app** (Apple's Passwords
app, Google Authenticator, Microsoft Authenticator, 1Password, …). A check lasts `AdminMfa.Hours` (12 by default),
however the admin signed in, Google included. Signing in itself doesn't ask for it, and viewers never see it.

An admin with neither is walked through setting one up the first time they open Admin: confirm the password, make a
passkey or scan the authenticator's QR code (on an iPhone or Mac, the link opens it straight in the Passwords app),
then keep the ten **recovery codes**, each good for one check if the passkey or phone is lost. Both can be set up, and
several passkeys (one per device; on Apple devices one passkey syncs through iCloud Keychain). They're managed under
**your name → Profile → Two-step verification**; adding or removing one, or making new recovery codes, asks for a
check with one you already have (a passkey, an authenticator code or a recovery code) in the last 10 minutes, not just
the one that keeps Admin open.
Five wrong codes in a row lock the account for 15 minutes, as wrong passwords do.

Passkeys need the site's HTTPS address (browsers only make them on secure pages), and MediaRiffle has to see the
request as HTTPS with the right host name: behind a reverse proxy, see `TrustedProxies` and `X-Forwarded-Proto` above.

**Locked out?** Another admin can clear someone's passkeys, authenticator and recovery codes with **Reset two-step** in
Admin → Users; they set it up again on their next visit. If you're the only admin, set `"AdminMfa": { "Required": false }`,
restart, reset it under Profile, and set it back.

## Libraries and categories

Admins add, edit and remove libraries under **Admin → Libraries**: a name, whether it holds shows, movies or music, its folder,
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

The player has a quality menu: **Auto**, **Original**, and, when allowed, 2160p (4K, 25 Mbit/s), 1440p (16) and 1080p High (14); 1080p (10), 720p (4),
480p (1.5), 360p (0.8). The menu lists only what a file can have here: nothing above the server's `MaxHeight`, the
user's cap, or (by more than a step) the picture itself.

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

Each user also has a **preference** for how Auto picks, under **Profile → Video quality** (an admin can set it for
them as their default, under **Admin → Users → Default quality**):

| | Auto then |
| --- | --- |
| **Auto** | Picks by the connection, as above. |
| **High** | Tries the best first: the file as it is when this device plays it, else the top level; steps down only if the connection can't keep up, and back up sooner. |
| **Medium** | Adapts up to 720p, never the file as it is: less data. |
| **Low** | Adapts up to 480p: the least data, for slow or metered connections. |

The cap always wins over the preference. Picking a quality in the player's menu overrides both for that device.

### 4K

A 4K file plays at 4K as it is (Original, or Auto with the connection to spare) on a device that decodes its video:
HEVC on Safari and on Chrome/Edge with a hardware HEVC decoder, H.264 everywhere. A converted stream stops at 1080p
unless the server allows 4K:

```json
"Transcoding": { "Encoder": "Nvenc", "MaxHeight": 2160, "MaxVideoKbps": 30000 }
```

That adds a 2160p level (H.264 at about 25 Mbit/s) to Auto and the menu, for browsers that can't decode HEVC
(Firefox) and for 4K Chromecasts. Use a hardware encoder (`Nvenc`, `Qsv` or `Vaapi`): `Libx264` on the CPU can't
make 4K in real time on most machines.

`"MaxHeight": 1440` (with `"MaxVideoKbps": 16000`) adds a **1440p** level instead, for a GPU that can't convert 4K in
real time but manages 1440p: an Intel Arc A380 converts a 60 Mbit/s 4K HDR disc rip to 1440p at 1.2x real time, to
4K at only 0.6x. `2160` offers both. Auto steps up to a level only while the server's encoder keeps it ahead of
playing, so on a busy server or a heavy file it stays at 1080p.

### HDR

HDR video (HDR10, HDR10+, HLG, and Dolby Vision's HDR10 base layer) sent as it is is the browser's to show, and
browsers handle it themselves (on an HDR screen, or tone-mapped on an ordinary one). Converted to H.264, it's
**tone-mapped** to ordinary (SDR, BT.709) colours first, so it isn't washed out and grey, as it would be with HDR's
picture simply relabelled. That's done on the CPU with ffmpeg's `zscale` (libzimg) and `tonemap` filters, after
scaling down, whatever the encoder; the log says at start if this ffmpeg lacks them (the builds linked from
ffmpeg.org and most Linux packages have them). It takes CPU time, most at 4K; `"ToneMapHdr": false` turns it off.

**libplacebo** does it better when this ffmpeg has it and a GPU with Vulkan works (the log says at start which is
used): it tone-maps on the GPU, and applies **Dolby Vision**'s metadata. That matters for Dolby Vision **profile 5**
(as streaming services use, and web rips carry): it has no ordinary picture underneath, so anything but a Dolby Vision
decoder shows it dark and green or purple. MediaRiffle never sends profile 5 as it is; it converts it, which looks
right only with libplacebo (without it, the log and the player's info line say so). Profiles 7 and 8 (Blu-ray discs,
most rips) carry HDR10 underneath and play as HDR10 does. libplacebo comes with gyan.dev's *full* build on Windows
(`winget install Gyan.FFmpeg`; the *essentials* build `Install-Ffmpeg.ps1` fetches may lack it), BtbN's builds, and
recent Linux packages; Vulkan comes with current NVIDIA, AMD and Intel drivers. `"UseLibplacebo": false` keeps zscale.
With the `Vaapi` encoder, libplacebo also tone-maps video decoded on the GPU (below).

### Decoding on the GPU

With a GPU encoder (`Qsv`, `Vaapi` or `Nvenc`), video is **decoded on that GPU** too and scaled there, so the picture
never comes back to the CPU. That's the difference between a 4K file converting several times faster than it plays and
not keeping up: decoding 4K HEVC is most of a transcode's work, and copying 4K frames back from the GPU costs about as
much. At start, MediaRiffle puts a tiny sample of each kind of video (H.264, HEVC, HEVC 10-bit, AV1, VP9, MPEG-2)
through the GPU and logs what it decodes, e.g. `Decoded on the GPU (Vaapi): H.264, HEVC, HEVC 10-bit, AV1, VP9`; the
player's info line says `decoded on the GPU` for a stream that is. Anything else is decoded on the CPU as before, as is
a stream the GPU fails on (the log says so), and one with an image subtitle burned in.

**HDR** is tone-mapped on the GPU path only with **`Vaapi`** (Linux): the GPU decodes and scales the video still in
10-bit, only those frames (a quarter of 4K's, at 1080p) come back, libplacebo tone-maps them on Vulkan (Dolby Vision 5
included, looking as it does on the CPU) and the GPU encodes the result. That needs Mesa's Vulkan driver
(`mesa-vulkan-drivers`, in the Proxmox template). An Intel Arc A380 converts a 60 Mbit/s 4K HDR disc rip to 1080p this
way at 1.7x real time; to 4K it manages only 0.6x, so leave `MaxHeight` at 1080 and play 4K files as they are (Original)
on devices that can. Quick Sync's and VA-API's own HDR tone mapping isn't used, as it comes out much darker. With
`Qsv` or `Nvenc`, HDR is decoded on the CPU and tone-mapped as described above (on an integrated GPU, copying frames
back saves nothing).

On Linux, Intel GPUs from Broadwell on (including the N100/N150 mini PCs) work with `Vaapi` and the
`intel-media-va-driver` package; AMD with `mesa-va-drivers`. In a Proxmox container, pass the GPU in first
([proxmox.md](proxmox.md)). `"HardwareDecoding": false` decodes everything on the CPU.

### Subtitles

Text subtitles (SRT, ASS, in the file or beside it) are shown by the browser over any stream. **PGS** subtitles, the
picture ones on Blu-ray discs and most disc rips, are drawn by the browser too (with libpgs), so turning them on or
off, or changing language, never restarts the video, and a 4K file keeps playing as it is. DVD (VobSub) subtitles,
and any picture subtitles on a Chromecast or AirPlay TV, are burned into a converted video instead, as before.

Getting a subtitle track out of a file means reading the whole file, as its subtitles lie among the video: a few
minutes for a 40 GB 4K disc rip. So MediaRiffle does it **ahead of time** (`PrepareSubtitles`): in the background,
newest files first, one at a time and only while nothing is streaming, every track of a file in one read, kept in
the data folder's `subtitles/` up to `SubtitleCacheGB`. A file not done yet gets its tracks the first time one is
turned on; a PGS track meanwhile shows five minutes at a time from where you are (read straight from that point, in
seconds), until the whole track is ready.

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

A show's other series (spin-offs, versions abroad) can share its folder: a season folder named for one,
`UK - Season 01`, `All Stars - Season 02`, `Down Under - S01` or `Global All Stars Season 01`, is that series'
season, and a file loose in the show's folder whose name isn't the show's (`drag.race.espana.s01e01.mkv` in
`Ru Pauls Drag Race`) is its own series too. The show's page then picks the series first, then its seasons.
Release tags (`1080p`, `x264`, `BluRay`, …) are dropped from titles.

## Music

A library of kind **Music** holds songs: MP3, AAC/M4A, FLAC, Ogg/Opus, WAV, WMA, ALAC, AIFF, APE, WavPack. It's browsed
by artist, then album, then track; **Play all** and **Shuffle** work on a library, an artist, an album or a category,
and picking a track plays its album from there. Music plays in a bar at the bottom of the page and carries on while
you browse; the bar's cover or title opens **Now playing**, with the queue to jump around in. Music isn't in Continue
watching.

Artists, albums and single songs go in Favorites and your lists like shows and movies (a song from Now playing, or its
☆ on the album page); a list of songs is a playlist, played in the order they were added. Artists, albums and songs
can be hidden like shows and episodes.

Songs are filed by their **tags** (artist, album artist, album, track and disc number, title, year), as written by
most rippers and taggers. Untagged files are read from their folders and names:

```
Music/
  Nirvana/
    Nevermind/
      cover.jpg                        (optional album art; folder.jpg also works, else the art inside the files)
      01 - Smells Like Teen Spirit.mp3
      02 - In Bloom.mp3
  Pink Floyd/
    The Wall/
      CD1/01 - In the Flesh.flac       (CD1, Disc 2, … folders are discs of the album)
```

An album is the tracks in one folder with the same album tag. Its artist is the **album artist** tag; without one,
it's the tracks' artist, or **Various Artists** when they differ (a compilation). A track's own artist is shown beside
it, and the artist's page lists the compilations they appear on.

A browser plays MP3, AAC and WAV as they are, and FLAC and Ogg where it supports them. Anything else is converted once
to AAC and kept in `audio` under `DataDirectory` (up to 2 GB; the least recently played go first), so it seeks like
any file.

**Chromecast**: the player bar has a Cast button (Chrome or Edge, on an HTTPS address, as for video). Casting hands
the TV the queue from the playing track on (up to 100 songs ahead, and handed the next stretch when it gets there), so
it carries on with the phone asleep; the bar and Now playing follow the TV and control it. Stopping casting brings the
music back to the page, paused where the TV was. The TV fetches each song and its cover from links that carry their
own key (the song and the account, encrypted with the server's keys, good for 24 hours and only while the account
exists), since it can't send the sign-in cookie; so, as for video, it has to be able to reach the server at the
address the page uses.
