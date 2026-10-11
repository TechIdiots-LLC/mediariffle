# Changelog

## master
### ✨ Features and improvements
- _...Add new stuff here..._

### 🐞 Bug fixes
- _...Add new stuff here..._

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
- _...Add new stuff here..._
