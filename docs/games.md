# Games

MediaRiffle can list a folder of game files (ROMs and disc images) and play them in the browser, with a controller or
the touch screen. The emulator runs on the viewer's device, not on the server: the server only sends the emulator, the
game and its BIOS, so playing costs it nothing but the download.

## 1. Add a Games library

Admin → Libraries → **Add a library**, kind **Games**, and the folder (a local path or a share, as for other
libraries). The games show under a **Games** tab, by system.

How a file's system is found (the same rules as 360 Riffle, so one games folder serves both):

- By its extension when that's the system's own: `.nes`, `.sfc`/`.smc`, `.gen`, `.gb`, `.gbc`, `.gba`, `.z64`, `.nds`,
  `.cso`, `.pce` …
- For extensions several systems or other programs use (`.zip`, `.7z`, `.bin`, `.iso`, `.cue`, `.chd`, `.m3u`, `.pbp`,
  `.col`), by the nearest folder named for a system: `SNES`, `Sega Genesis`, `SEGA_GENESIS`, `PSX`, `PlayStation`,
  `PSP`, `DS` … A zip outside such a folder is looked into: it counts when all the games in it are one system's.
- Skipped: folders named `bios`, `firmware`, `emulator(s)`, `boxart`, `covers`, `snaps`, `screenshots`, `manuals`,
  `saves`, `states`, and any folder with an `.exe` or `.dll` and no games of its own (an emulator kept beside the games).
- A zip next to the same game unpacked (`Game.zip` and `Game.nds`) is listed once.
- A `.cue` takes its tracks along, an `.m3u` its discs: they're played as one game, sent to the browser zipped.

### Versions of a game

Sets often have a game many times over: regions, revisions, bad dumps, hacks, translations. Files of one system whose
names are the same without their tags (`Zelda (U) (V1.1) [!]`, `Zelda (G) [b1]`, `Zelda (U) [T+Spa]`, a DS set's
`2333 Kung Fu Panda (US)` and `2418 Kung Fu Panda (DE)`) are one game: one tile, named without the tags, saying how many
versions there are. The tile opens the best of them: a good dump before a bad one, a hack, a translation or a beta;
then USA, World, Europe, untagged, Japan; a verified dump (`[!]`); the later revision.

The game's page has a **Version** list, best first, with each one's tags in words ("USA · v1.1 · verified", "Germany ·
bad dump", "USA · Spanish translation 1.0 by DJTraducciones"); picking one opens it. Each version keeps its own saves
in the browser. Favorites and lists hold the version that was added; a list with two versions of a game shows it once.

### Box art

A picture named like the game beside it wins: in its folder, or in a `boxart`, `covers`, `images`, `Named_Boxarts`
(libretro thumbnails) or `media/images` folder there (`.png`, `.jpg`, `.webp`).

For the rest, the server looks the game up in the [libretro-thumbnails](https://github.com/libretro-thumbnails)
project (thumbnails.libretro.com). Names are compared on their letters and digits, with tags in brackets, DS sets'
numbers and "The" left aside, so GoodTools names (`7th Saga, The (U) [!].smc`) find No-Intro ones (`7th Saga, The
(USA)`); the game's own region is preferred, then USA, World, Europe, Japan. A picture is fetched once, made a JPEG of
at most 512 pixels (about 65 KB) and kept in `gameart` under the data folder for good, so the art of the games you
have stays if the service ever goes. A background pass works through new games after each scan (a few a second);
tiles shown before their turn are looked up right away. Each system's list of names is read again after 30 days, and
the games that had no match then get another look. Admin → Games shows how many were found. Arcade games aren't
looked up (their files are named by romset, the art by title). `Games.BoxArt: false` turns this off.

## 2. Add the emulator

The emulator is [EmulatorJS](https://github.com/EmulatorJS/EmulatorJS) (GPL-3.0) with RetroArch's cores, built for the
browser. They aren't part of MediaRiffle: an admin adds them under **Admin → Games**, into MediaRiffle's data folder
(`emulatorjs/`).

- **Download**: MediaRiffle fetches EmulatorJS 4.2.3 (about 2 MB) and the cores for the systems you have games for (4
  to 15 MB each) from the npm registry, checking each against the registry's SHA-512. The server needs to reach
  `registry.npmjs.org`. A core can be added on its own from its row.
- **Upload**, for a server without internet: on another computer get `@emulatorjs/emulatorjs@4.2.3` and the
  `@emulatorjs/core-…@4.2.3` packages you need (`npm pack @emulatorjs/core-snes9x@4.2.3`, or the download link on
  npmjs.com), and add the `.tgz` files under "No internet on this server?".

| Core | Plays |
| --- | --- |
| fceumm | NES |
| snes9x | SNES |
| genesis_plus_gx | Genesis / Mega Drive, Master System, Game Gear, Sega CD |
| picodrive | 32X |
| gambatte | Game Boy, Game Boy Color |
| mgba | Game Boy Advance |
| mupen64plus_next | Nintendo 64 |
| melonds | Nintendo DS |
| pcsx_rearmed | PlayStation |
| ppsspp | PSP (needs a browser with threads: see below) |
| mednafen_pce, mednafen_ngp, mednafen_wswan, handy, beetle_vb | PC Engine (and CD), Neo Geo Pocket, WonderSwan, Lynx, Virtual Boy |
| stella2014, a5200, prosystem, virtualjaguar, gearcoleco | Atari 2600, 5200, 7800, Jaguar, ColecoVision |
| yabause | Saturn |
| fbneo | Arcade (FinalBurn Neo romsets) |

3DS, PS2, GameCube, Wii and Dreamcast games are listed, but no emulator for them runs in a browser.

The cores have their own licences, some of them non-commercial (Snes9x, Genesis Plus GX, PicoDrive, FinalBurn Neo):
fine for a home server, worth knowing before offering games to others.

## 3. BIOS files

Some systems need the console's own BIOS, which only you can provide; Admin → Games lists them with the file name the
core looks for. Required: Sega CD (`bios_CD_U.bin`), PC Engine CD (`syscard3.pce`), Lynx (`lynxboot.img`), Atari 5200
(`5200.rom`), ColecoVision (`colecovision.rom`). Optional (the core has a stand-in, a few games do better with the
real one): PlayStation (`scph5501.bin`, or `scph1001.bin`), Game Boy Advance (`gba_bios.bin`), DS (`bios7.bin`,
`bios9.bin`, `firmware.bin`), Saturn (`saturn_bios.bin`). They're sent only to signed-in users, to play.

## Playing

A game's page has **Play**, which opens the game page; the browser keeps a copy of the game, so the next start is
quicker. Press **Start** (browsers want a tap or click before games may make sound).

- **Controllers**: Xbox, PlayStation and most others, by USB or Bluetooth, in Chrome, Edge, Firefox and Safari
  (Windows, Android, iPhone and iPad). Press a button once on the game page so the browser shows it to the game. The
  emulator's menu (bottom bar) has the controls to remap, save states, full screen and the core's settings.
- **Buttons**, at the top of the game page, chooses how your controller's face buttons meet the console's, for all of
  that system's games: **by letter** (an Xbox A presses A, as EmulatorJS does by itself; on a Nintendo pad A is the
  right button, so the places differ) or **by place** (the bottom button presses the console's bottom one: B on
  Nintendo pads, Cross on a PlayStation). Remap in the emulator's Controls menu and the page offers to use that for
  all the system's games too. It's kept on the server, per user, so it follows you to every device.
- **Touch**: on a phone without a controller, buttons are drawn over the game.
- **iPhone**: Safari can't make the game fill the screen; Share → Add to Home Screen opens MediaRiffle without
  Safari's bars. Large disc games (PlayStation, PSP) can be more than Safari lets one page hold.
- **Saves**: the game's own save (a cartridge's battery save, a memory card) is kept on the server, per user, as
  well as in the browser, so you carry on from any device. The emulator writes it every 30 seconds and when you
  leave (Back waits for it); the time it was last kept shows at the top. When a game starts, the newer one is used:
  the server's if it was saved on another device, this browser's if you played here since. A save from before
  MediaRiffle kept them is taken up the first time. Saves go by the game's system and file name, so they stay when a
  game is moved or its library scanned again. `Games.SpacePerUserMB` can limit the space each user's saves take.
- **Save states** are kept in that browser.

The game page has its own security settings, looser than the rest of the site's because the emulator needs them:
WebAssembly and `'unsafe-eval'` (the cores evaluate code), still only this site's own scripts, and cross-origin
isolation, which the PSP core needs for threads.

## Behind a reverse proxy

Uploading the packages sends files of up to 15 MB (200 MB at most). nginx takes 1 MB by default: add
`client_max_body_size 200m;` to the `location`. IIS takes 30 MB (`maxAllowedContentLength`), enough for every core.
Games themselves are only downloaded.
