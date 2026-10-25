# Updating MediaRiffle

**Admin → Updates** shows the version running, any newer release with its notes, and an **Update to …** button. It
works for installs from the `.deb` (Ubuntu, Debian, the Proxmox template) and from the Windows installer
(`-setup.exe`). The Windows `.zip` and the Linux `.tar.gz` are updated as they were installed: see their install
guides.

MediaRiffle looks for new releases every 12 hours and when you press **Check now**. Settings, accounts, libraries,
lists and watch positions are kept. The server restarts, so anything playing stops for a minute or so.

The first release with Admin → Updates has to be installed by hand, as before ([Proxmox](proxmox.md#upgrading-to-a-new-release),
[Ubuntu](install-ubuntu.md), Windows: run the setup). After that, Admin can update it.

## What happens

MediaRiffle runs under an account that can't install software, so the update itself is done by a small updater that
runs with the rights to:

- **Linux:** `/usr/lib/mediariffle/mediariffle-update`, as root, started by `mediariffle-update.path` when MediaRiffle
  writes `/var/lib/mediariffle/update/request`.
- **Windows:** `Update-MediaRiffle.ps1` in the program folder, as SYSTEM, by the **MediaRiffle Update** scheduled task.
  The task starts on the event (4100, source MediaRiffle) MediaRiffle writes to the Application log when you press
  the button, and at startup.

MediaRiffle only tells the updater which version you picked. The updater:

1. Refuses any version that isn't newer than the one installed.
2. Downloads that release's `.deb` or setup and its `SHA256SUMS` from the project's own address on GitLab (or GitHub,
   see below), and checks the file against them. On Windows, if the installed program is signed, the setup must be
   signed by the same publisher.
3. Downloads the installed version's `.deb` or setup too, to go back to.
4. Stops MediaRiffle, saves a copy of its database, and installs the new version (`apt-get install`, or the setup with
   `/S`).
5. Waits up to three minutes for the new version to start. If it doesn't, it puts the database back and reinstalls
   the old version.

Admin shows each step while it happens, and the page reloads once the new version answers.

The updater's log is `/var/log/mediariffle-update.log` on Linux and `%ProgramData%\MediaRiffle-update\update.log` on
Windows.

## Settings

In the `Updates` section of the settings file (see [configuration](configuration.md)):

| Setting | Meaning |
|---|---|
| `Updates.Source` | `GitLab` (the project, the default) or `GitHub` (its mirror). Where releases are looked for and downloaded from. |
| `Updates.Prereleases` | `true` to be offered pre-releases (`1.2.0-rc.1`) too. `false` by default. |
| `Updates.CheckHours` | How often to look for a new release. `0` looks only when you press **Check now**. 12 by default. |

## When it doesn't work

- **"Waiting for the updater to start" stays.** The updater isn't running.
  - On Linux, check `systemctl status mediariffle-update.path` (it should be active; `systemctl enable --now
    mediariffle-update.path` if not).
  - On Windows, check that Task Scheduler has **MediaRiffle Update**. Running the installer again creates it.
- **"has no … with checksums to update from."** That release was published before releases carried `SHA256SUMS`.
  Install it by hand once.
- **It went back to the old version.** The log says why. The release notes may say what the new version needs.

A snapshot or backup before an update is still a good idea, especially for a container (`pct snapshot`).
