# Changelog

## master
### ✨ Features and improvements
- _...Add new stuff here..._

### 🐞 Bug fixes
- _...Add new stuff here..._

## 0.3.1
### ✨ Features and improvements

### 🐞 Bug fixes
- **The Cast button** is the size of the other buttons and drawn in their colour, instead of a huge black icon that
  filled the space it was given.

## 0.3.0
### ✨ Features and improvements
- **AirPlay** (Safari on Mac, iPhone and iPad): an AirPlay button appears when Safari finds a receiver, such as an
  Apple TV or a TV or Roku with AirPlay 2, which then fetches the stream itself as a Chromecast does. Safari now plays
  HLS with its own player instead of hls.js, which AirPlay needs. An x265 stream sent as it is switches to H.264 when
  AirPlay starts, since not every receiver decodes HEVC.
- **On a TV…** in the player: the ways to get the picture onto a TV from this device, Cast, AirPlay, screen mirroring
  (Win+K on Windows, Screen cast on Android, a Roku's screen mirroring) or HDMI, with the ones that fit it first.
- **Full screen** uses the whole screen for the video: the controls lie over its bottom and fade out, with the mouse
  pointer, after 3 seconds without moving the mouse (unless paused or the mouse is over them). Moving the mouse,
  tapping or a key brings them back.
- **Player buttons** in the order play/pause, previous, next, back 10 s, forward 30 s, stop.

### 🐞 Bug fixes

## 0.2.0
### ✨ Features and improvements
- **Standard player controls**: previous, back 10 s, play/pause, forward 30 s, stop and next, plus volume and mute
  (remembered). Previous and next follow the play queue when one is playing, otherwise the show's episode order or a
  movie's parts; previous goes back to the start first when more than a few seconds in. Stop ends the stream (freeing
  the server) and Play picks up where it stopped. Clicking the video plays or pauses, double-clicking goes full screen.
- **Keyboard and media keys**: space or K play/pause, ← → seek 10 s, ↑ ↓ volume, M mute, F full screen, N / P next and
  previous; the keyboard's media keys and the system's media panel work too.
- **Admin → Libraries updates by itself**: file counts, each library's scan status (done, files it couldn't read,
  folder not available) and the scan's progress ("Movies: 340 of 1601 files read") refresh every few seconds while the
  page is open. Scan now sits beside the libraries.
- **Install-Ffmpeg.ps1** (in the program folder and the Windows zip): downloads ffmpeg from gyan.dev, checks it
  against the published SHA-256, installs it to C:\Tools\ffmpeg, points the settings at it and restarts the service.

### 🐞 Bug fixes
- **Full screen** takes the controls along and centres the video, letterboxed, both ways: the video used to sit at the
  top of the screen, so a TV that overscans cut off the top, and there were no controls.
- **Pictures** are taken from a few minutes in, through ffmpeg's thumbnail filter, instead of an early frame that was
  often black (or the very first frame, for files whose length wasn't known). Pictures made before are replaced.
- **A file that couldn't be read** at the last scan (e.g. because ffmpeg wasn't installed yet) is read again when it's
  opened, instead of waiting for the next scan, and "ffprobe isn't installed" is said as such.

## 0.1.1
### ✨ Features and improvements

### 🐞 Bug fixes
- **The installed Windows service works over plain HTTP**: cookies were HTTPS-only outside development, so
  http://localhost:5080 couldn't start a session. They're now secure whenever the request is HTTPS, including behind a
  trusted proxy that says so.
- **Windows event log**: the installer registers the MediaRiffle event source, which the service's own account can't.

## 0.1.0
### ✨ Features and improvements
- **First release.** Streams a video library to browsers and Chromecasts, the successor of the 2014 Tongue project.
- **Libraries** of TV shows (Show / Season NN / SxxEyy) and movies (including cd1/cd2 parts), on local folders, mounts or
  file shares, managed by admins in the browser with a folder check before saving.
- **Playback** the cheapest way each device allows: the file as it is, H.264 copied into HLS, or a transcode to H.264
  (CPU, NVENC, Quick Sync or VA-API). x265 is sent as it is to devices that decode HEVC. Seeking anywhere, resume,
  continue watching, next episode.
- **Quality**: Auto (follows the connection), Original, 1080p, 720p, 480p, 360p, switched at the same spot; per-user
  caps.
- **Audio tracks and subtitles**: subtitles from the file or side-car .srt/.vtt/.ass as WebVTT, image subtitles burned
  in.
- **Categories**: tags per library, as in Media Library Manager, to browse, play through or shuffle.
- **Chromecast** (Default Media Receiver).
- **Accounts by invitation only**, lockout and rate-limited sign-in, strict cookies, antiforgery and Content Security
  Policy.
- **Installers**: a Windows service installer (x64, arm64) and a portable zip (IIS); a .deb for Ubuntu/Debian and a
  .tar.gz with install.sh for other distributions (x64, arm64).

### 🐞 Bug fixes
