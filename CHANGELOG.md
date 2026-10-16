# Changelog

## master
### ✨ Features and improvements
- **Full screen stays full screen from one video to the next**: Next, the play queue moving on, a media key or a voice
  assistant's "next track" (e.g. through Home Assistant) no longer drops out of full screen. Each new one shows its
  title (and its place in the queue) over the picture for a few seconds, and a queue's countdown to the next one shows
  there too.
- **Hide in full screen**: a Hide button in the full-screen controls hides the episode (or movie) for you and goes on
  to the next one.
- **Preferred video quality**: each user picks how Auto plays under Profile → Video quality: Auto (by the
  connection), High (the best first), Medium (up to 720p) or Low (up to 480p, to save data). An admin can set it as a
  user's default under Admin → Users → Default quality, beside the Best quality cap, which still wins.
- **A Back button** in the top bar, beside the name (phones and the home-screen app have no browser back to tap): back
  to the page before, or home when MediaRiffle was opened straight onto this one.

### 🐞 Bug fixes
- **Skip back and forward are both 10 seconds** (forward was 30), on the buttons and on media keys.
- **The volume slider reaches 100%**: sliders were styled like text boxes, so the track ended short and full volume
  looked like about 90%.
- **Pages open at the top**: going to a show (or any page) after scrolling another one opened it as far down as the
  last one was scrolled.
- **Upgrades keep settings left in the program folder**: settings put in the program folder's `appsettings.json`
  (rather than `%ProgramData%\MediaRiffle\appsettings.json` or `/etc/mediariffle/appsettings.json`) were lost when an
  upgrade replaced it. The Windows installer, the `.deb` and `install.sh` now save it first, and the new version
  carries whatever wasn't a default (Google sign-in, trusted proxies, …) into the settings file, which wins where it
  set something itself (backed up first). That file no longer lists the defaults, and the log warns at start about
  settings left there.
- **Adding a two-step method asks for one you have**: with a passkey or authenticator set up, adding another (or
  removing one, or new recovery codes) wants a check with an existing one in the last 10 minutes; the hours-old check
  that keeps Admin open was enough before, so a session left open could add its own passkey.

## 0.5.1
### ✨ Features and improvements
- **Switch user**: everyone signed in on a device (with "Keep me signed in") is listed under your name → Switch user
  and on the sign-in page, to switch between without a password, like Google accounts or Netflix profiles. Add another
  account from there; sign out one or all. An account can ask for a PIN before others switch into it (Profile → Switch
  user PIN). Admin still asks for its own two-step check after a switch. See docs/configuration.md.
- **Two-step verification for Admin**: Admin asks for a passkey (Face ID, Touch ID, Windows Hello, a phone or a
  security key) or an authenticator app's code (Apple Passwords, Google or Microsoft Authenticator, 1Password, …), once
  every 12 hours. An admin without one is walked through setting it up the first time they open Admin, ending with ten
  one-time recovery codes. Manage passkeys, the authenticator and recovery codes under Profile; another admin can reset
  someone's under Admin → Users. Signing in itself is unchanged. See docs/configuration.md.
- **4K transcodes**: a 2160p level (H.264 at about 25 Mbit/s) for Auto and the quality menu, when the server allows it
  (`Transcoding:MaxHeight` 2160, `MaxVideoKbps` about 30000; best with a hardware encoder). For browsers that can't
  play a 4K file's HEVC as it is, and 4K Chromecasts. The quality menu now lists only the levels a file can have.
- **HDR tone mapping**: HDR video (HDR10, HLG) converted to H.264 is tone-mapped to ordinary colours instead of
  looking washed out. Needs ffmpeg's zscale filter, which most builds have (the log says if not);
  `Transcoding:ToneMapHdr` turns it off. See docs/configuration.md.

### 🐞 Bug fixes
- 4K transcodes and HDR tone mapping ([!8](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/8)) (@acalcutt)
- Switch user: accounts signed in on a device, with an optional PIN ([!7](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/7)) (@acalcutt)
- Admin two-step verification (passkeys / authenticator app), and shows with several series ([!5](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/5)) (@acalcutt)
- **A show's other series are told apart**: season folders named for another series of the show (`UK - Season 01`,
  `All Stars - Season 02`, `Down Under - S01`, `Global All Stars Season 01`) used to be merged into the show's own
  seasons by the episode numbers in the file names. Each is its own series now, and files loose in the show's folder
  named for another series (`drag.race.espana.s01e01.mkv`) are too. A show with several picks the series first, then
  its seasons; Play all and the next episode go series by series, the show's own first. Episodes already in the
  library are filed again at the next scan, without reading the files again.
- **Episode thumbnails stay in their place**: a frame was shown at its full size and covered the episode's title.

## 0.5.0
### ✨ Features and improvements
- **License**: MediaRiffle is released under the BSD 3-Clause License, copyright TechIdiots LLC. `LICENSE` ships
  with every package (in the program folder, and as `/usr/share/doc/mediariffle/copyright` in the `.deb`).
- **Sign in with Google**: link your Google account under your name → Profile, then sign in with it instead of your
  password. Google only signs in accounts that linked it; people still join with invites. Off until an admin sets
  `MediaRiffle:Google:ClientId` and `ClientSecret` (see docs/configuration.md).
- **A menu under your name** in the top bar, with Profile and Sign out.
- **Music libraries**: a new library kind, browsed by artist, album and track. Songs are filed by their tags (album
  artist, album, disc and track number, year), or by `Artist/Album/01 - Title` folders and names; tracks with
  different artists and no album artist make a "Various Artists" compilation. Play all and Shuffle work on a library,
  an artist, an album or a category, and picking a track plays the album from there. MP3, AAC and WAV (and FLAC and
  Ogg where the browser plays them) are sent as they are; anything else is converted to AAC once and kept. Cover art
  from `cover.jpg` / `folder.jpg`, else the picture inside the files. See docs/configuration.md.
- **Music keeps playing while you browse**, in a player bar at the bottom of every page (cover, title, previous /
  play / next, seek, close), on the lock screen and media keys too. **Now playing** shows it bigger, with the queue to
  jump around in. The playing track is marked on its album page; after a reload the music is back, paused where it
  was. Starting a video closes it.
- **Music on a Chromecast**: the player bar's Cast button sends the queue to the TV, which plays through it itself
  (on with the phone asleep); the bar and Now playing follow and control it, and stopping casting brings the music
  back, paused where the TV was. The TV fetches songs and covers from 24-hour links that carry their own key, as it
  can't sign in. FLAC and Ogg go to it as they are.
- **Favorites, lists and hiding for music**: artists, albums and single songs go in Favorites and your lists (a
  song's ☆ on its album page, or its lists on Now playing); a list of songs is a playlist, played in the order they
  were added, and a song can be taken out with ×. Artists, albums and songs can be hidden like shows and episodes.
- **Hide what you don't want to see**: a Hide button on a show's or movie's page, on each episode, and while playing.
  Hidden things stay listed, greyed out (Unhide brings them back), but are left out of Play all, Shuffle (categories,
  lists, libraries and shows), Continue watching and the next episode. Only you see what you hid. Profile → Hidden
  lists everything you hid, to open or unhide in one place.

### 🐞 Bug fixes
- Music ([!3](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/3)) (@acalcutt)
- Google auth ([!2](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/2)) (@acalcutt)

## 0.4.0
### ✨ Features and improvements
- **Favorites and your own lists**: a ☆ Favorite button and an "Add to a list…" box on a show's or movie's page (and
  while playing one). Everything you tag the same way is one of your lists, to browse, play through or shuffle like
  the admins' categories; only you see them. The home page shows your Favorites and your lists. They go with the show
  or movie when it leaves the library, and with the account.
- **Auto quality is adaptive streaming**, as on streaming sites: the player (hls.js, or Safari, a Chromecast or an
  AirPlay receiver itself) times each segment's download and moves between 1080p, 720p, 480p and 360p segment by
  segment, down as soon as the connection can't keep up and back up when it can, without restarting. The server makes
  only the level being watched, from where it's watched, one ffmpeg per viewer, replaced when the player switches or
  seeks. When the connection clearly has room for the file as it is, Auto still plays that (no transcode), and moves
  to the adaptive stream when it stops keeping up (seen from the buffer running down, before it runs out).
- **Seeking a transcode is instant** within what's been made, and anywhere else no longer restarts the stream: picked
  levels are adaptive streams of one level.
- **Admin → Users: change a user's role**, viewer to admin or back, from the Role column. Your own role can only be
  changed by another admin. The change reaches the user's open sessions within a minute, without signing in again.

### 🐞 Bug fixes
- **A paused player keeps its stream**: it used to be stopped as idle after 90 seconds, so playing again failed. A
  stream that goes away anyway (e.g. the server restarted) is started again where it was.
- **Cast** no longer throws an error when the browser says it can cast but Google's Cast framework didn't load.

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
