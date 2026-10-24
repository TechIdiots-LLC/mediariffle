# Changelog

## master
### ✨ Features and improvements
- **Bring back lists, favorites and watch history after a move**: `deploy/export-user-data.sql` (run on the old
  database, in DB Browser for SQLite or sqlite3) makes an import script for the new one; `deploy/carry-user-data.sql`
  does it in one step with both databases at hand. They carry them from an
  old database into one whose libraries were scanned afresh (moved to a new server, or a library's folder changed in
  Admin), matching shows, movies, music and files by what they are rather than by id. See docs/proxmox.md.

### 🐞 Bug fixes
- **4K HDR on an Intel Arc (VA-API) converts at 1.7x real time**: the GPU now decodes and scales it, and only the
  1080p frames are copied back for libplacebo and encoded on the GPU again. Handing VA-API's 10-bit frames straight to
  Vulkan crashed on Arc with Ubuntu 24.04's Mesa, so HDR fell back to the CPU and couldn't keep up. The startup
  libplacebo check waits long enough for a first run's shader compile (it gave up after 30 s), and with VA-API, HDR
  decoded on the CPU is tone-mapped with libplacebo too rather than zscale.

## 0.7.0
### ✨ Features and improvements
- **Proxmox LXC template**: each release has `MediaRiffle-v<version>-proxmox-amd64.tar.zst`, Proxmox's Ubuntu 24.04
  template with MediaRiffle, ffmpeg and the Intel/AMD GPU drivers installed, listening on port 5080; a container made
  from it sets itself up at first boot (port and trusted proxies from `/etc/mediariffle/firstboot.env`). Built and
  boot-tested by the release pipelines (GitLab's on the Linux runner). See docs/proxmox.md, which also covers passing in
  a media share and the host's GPU.
- **Decoding on the GPU**: with a GPU encoder (`Qsv`, `Vaapi`, `Nvenc`), video is decoded and scaled on that GPU too,
  so the picture never leaves it: a 4K file converted about 3.5 times faster on an Intel laptop's GPU. With `Vaapi`
  (Linux), HDR and Dolby Vision are tone-mapped there with libplacebo, through Vulkan. What the GPU decodes is found
  out at start and logged; anything else, a stream the GPU fails on, and burned-in subtitles are decoded on the CPU as
  before. `Transcoding:HardwareDecoding` turns it off. See docs/configuration.md.
- **Play in VLC**: under the player, Play in VLC… opens the file as it is in VLC, from where you are (on Android and
  an iPhone or iPad the VLC app opens; on a computer a small .m3u playlist is saved, or copy the link into VLC's Open
  Network Stream). VLC decodes everything itself (4K HEVC, HDR, Dolby Vision, TrueHD), so the server only reads the
  file. The link carries its own key, works for 24 hours, and isn't offered to an account with a quality cap.
- **Smoother 4K played as it is**: the browser keeps less of what's already played of a high-bitrate stream, so its
  limited room goes to what's ahead.

### 🐞 Bug fixes
- Av sync ([!20](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/20)) (@acalcutt)
- **The Cast button shows again**: Google's own button hid itself until Chrome had found a Chromecast. The player
  and the music bar now have a Cast button of their own, always there in Chrome and Edge (on HTTPS), which opens
  Chrome's Cast dialog and reads "Casting" while casting. "Watching on a TV…" moved out of the controls, under the
  player with Play in VLC….
- **Sound out of sync after a seek, resume or subtitle change**: a file played as it is (copied video, as 4K HEVC
  is) and started part-way through had its sound a second or more early, and text subtitles late. Copied video
  starts at a keyframe before the time; the sound now starts there with it, and the player's clock knows where.
  (Seeking to that keyframe itself had made ffmpeg land on the one before it.)

## 0.6.2
### ✨ Features and improvements
- **Dolby Vision, and better HDR tone mapping with libplacebo**: when the server's ffmpeg has libplacebo and a Vulkan
  GPU works (checked at start), converted HDR is tone-mapped with it on the GPU, applying Dolby Vision's metadata, and
  HDR10 looks more natural than with zscale. Dolby Vision profile 5 (streaming services, web rips), which has no
  ordinary picture underneath and showed dark and green or purple, is never sent as it is any more: it's converted.
  Profiles 7 and 8 still play as the HDR10 they carry. `Transcoding:UseLibplacebo` turns it off. See docs/configuration.md.

### 🐞 Bug fixes
- Dolby vision ([!18](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/18)) (@acalcutt)
- **Auto quality settles down**: it kept switching levels on files the server converts at only a little faster than
  they play (4K HDR, often). Waiting for ffmpeg to make a segment no longer counts as a slow connection, Auto no longer
  steps up to a level ffmpeg can't keep up with (it tries again after 10 minutes), steps up only with plenty of room,
  and stays on a level 20 s before stepping up from it. Safari's own player and a Chromecast pick levels as before.

## 0.6.1
### ✨ Features and improvements
- **Change your password** under Profile → Password (the current one first, and with two-step verification set up, a
  passkey or code too; your other devices are signed out, this one stays signed in).
- **Password reset links**: Admin → Users → Reset password makes a one-time link (good for 24 hours) for someone who
  forgot theirs; they open it and choose a new password, so the admin never knows it. It signs them out everywhere
  else and lifts a lockout.
- **Profile pictures**: everyone starts with a built-in cartoon picture (a llama, a sloth, a taco, …) their account
  picks, and can choose another or use their own photo (cropped and shrunk in the browser; iPhone photos work) under
  Profile. It shows beside your name, under Switch user, and in Admin → Users.
- **Delete or rename a list**: on a list's page, Delete list takes everything off it at once (it all stays in the
  library), and Rename gives it a new name (an existing list's name puts the two together). Favorites can be cleared.
- **Take something off Continue watching**: an × on each one there. It isn't hidden: the place is kept, and watching
  it again puts it back.

### 🐞 Bug fixes
- Passwords ([!16](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/16)) (@acalcutt)
- **Stopping at the credits counts as watched**: the last 8%, or the last 3 minutes of anything over 10 minutes (a
  long movie's credits ran past the old 7%), so those don't linger in Continue watching; ones saved before drop off too.

## 0.6.0
### ✨ Features and improvements
- **Full screen stays full screen from one video to the next**: Next, the play queue moving on, a media key or a voice
  assistant's "next track" (e.g. through Home Assistant) no longer drops out of full screen. Each new one shows its
  title (and its place in the queue) over the picture for a few seconds, and a queue's countdown to the next one shows
  there too.
- **Hide from the player**: a Hide button in the player's controls (full screen or not) hides the episode (or movie)
  for you and goes on to the next one. Coming from a play queue, the title and its place in the queue show over the
  picture for a few seconds, full screen or not.
- **The player fits the screen, from a phone to 4K**: on a large screen the picture grows to fill its height (the
  player's page isn't held to 1400px wide any more), and the controls' text grows a little to stay readable. On a phone
  the picture runs edge to edge, the controls wrap in tidy groups (quality, audio and subtitles on a row of their own),
  and the height is right in iPhone Safari (its toolbars are no longer counted). iPhone and iPad, which only take the
  volume from their buttons, show just Mute.
- **Preferred video quality**: each user picks how Auto plays under Profile → Video quality: Auto (by the
  connection), High (the best first), Medium (up to 720p) or Low (up to 480p, to save data). An admin can set it as a
  user's default under Admin → Users → Default quality, beside the Best quality cap, which still wins.
- **A Back button** in the top bar, beside the name (phones and the home-screen app have no browser back to tap): back
  to the page before, or home when MediaRiffle was opened straight onto this one.

### 🐞 Bug fixes
- Player: Hide outside full screen too; fits the screen from a phone to 4K ([!14](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/14)) (@acalcutt)
- Player: full screen across videos, Hide, preferred quality; a Back button ([!13](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/13)) (@acalcutt)
- Two-step: adding a method asks for one you already have ([!12](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/12)) (@acalcutt)
- Upgrades keep settings left in the program folder ([!11](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/11)) (@acalcutt)
- Scroll top ([!10](https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/merge_requests/10)) (@acalcutt)
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
