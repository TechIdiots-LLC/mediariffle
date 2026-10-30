# MediaRiffle on Proxmox

Each release includes `MediaRiffle-v<version>-proxmox-amd64.tar.zst`, a Proxmox LXC template: Proxmox's own Ubuntu
24.04 template with MediaRiffle's `.deb` installed, ffmpeg with it, and the drivers for encoding on an Intel or AMD GPU
(VA-API, and Vulkan for libplacebo). MediaRiffle listens on port 5080 of the container's network. A container made from
the template sets itself up on its first boot (its port and the reverse proxies it trusts, from `firstboot.env`), and
MediaRiffle makes its database and keys when it first starts, so every container is its own.

Container ID `120`, host names and addresses here are placeholders; `<version>` is the release, e.g. `0.7.0`.

## Before you start

- **Size:** 2 cores and 2 GB of RAM for playing files as they are; 4 cores and 4 GB for converting. Converting 4K wants
  the node's GPU (below) more than more cores. 16 GB of disk: Ubuntu and MediaRiffle take about 2 GB, and conversions
  are written to `/var/lib/mediariffle/transcode` while they play (about 1 GB an hour of 1080p each) and deleted after;
  kept subtitle tracks take up to `SubtitleCacheGB` (5 GB by default) in `/var/lib/mediariffle/subtitles`.
- **The media:** an unprivileged container can't mount a Windows or Samba share itself. Mount it on the Proxmox node and
  pass the folder in (below).
- **Moving an existing install?** Read [Moving an existing install into the container](#moving-an-existing-install-into-the-container)
  first: it keeps your accounts, watch history and lists.

## Creating the container

1. Download the template into the node's template storage:

   ```bash
   cd /var/lib/vz/template/cache
   wget https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/releases/v<version>/downloads/MediaRiffle-v<version>-proxmox-amd64.tar.zst
   ```

   (Or upload it on the web UI: *local → CT Templates → Upload*.)

2. Create the container:

   ```bash
   pct create 120 local:vztmpl/MediaRiffle-v<version>-proxmox-amd64.tar.zst \
       --hostname mediariffle --ostype ubuntu --unprivileged 1 --features nesting=1 \
       --cores 4 --memory 4096 --swap 1024 --rootfs local-lvm:16 \
       --net0 name=eth0,bridge=vmbr0,ip=dhcp --onboot 1
   ```

   On the web UI, *Create CT* works the same way; *Nesting* (under *Options → Features*) is ticked by default for an
   unprivileged container. `nesting=1` lets systemd sandbox MediaRiffle's service in the container. A fixed address
   (`ip=192.168.0.120/24,gw=192.168.0.1`) suits a reverse proxy better than DHCP.

3. **Optional:** set the first-boot settings *before* the first start: the port, and the reverse proxies in front (see
   [Behind HAProxy](#behind-haproxy-on-opnsense-or-pfsense-or-another-proxy)):

   ```bash
   pct mount 120
   nano /var/lib/lxc/120/rootfs/etc/mediariffle/firstboot.env
   pct unmount 120
   ```

   Uncomment the lines you need: `MEDIARIFFLE_PORT`, `MEDIARIFFLE_TRUSTED_PROXIES`, `MEDIARIFFLE_FORWARD_LIMIT`.

4. Pass the media in ([below](#media-from-a-file-share)) and, to convert on the GPU, the GPU
   ([below](#encoding-and-decoding-on-the-gpu)). Both can be done later too.

5. Start it, and see where it answers:

   ```bash
   pct start 120
   pct exec 120 -- cat /root/mediariffle-first-login.txt
   ```

   That lists its addresses, e.g. `http://192.168.0.120:5080/`. **The first visit creates the admin account**, so open
   it straight away, before anyone else on the network can. Then add the libraries under **Admin → Libraries**
   (`/mnt/media/Movies` and so on).

## Behind HAProxy on OPNsense or pfSense (or another proxy)

The proxy handles HTTPS and the public name; MediaRiffle has to know its address, or every visitor looks like the proxy
(and sign-in rate limiting throttles everyone at once). Put it in `firstboot.env` before the first start:

```bash
MEDIARIFFLE_TRUSTED_PROXIES="192.168.0.1"
```

For an HA pair with CARP, list both firewalls' own addresses on that network and the CARP VIP. HAProxy connects from
the active node's own address, so the VIP alone isn't enough:

```bash
MEDIARIFFLE_TRUSTED_PROXIES="192.168.0.2 192.168.0.3 192.168.0.1"
```

In HAProxy:

- the frontend has the certificate (the ACME plugin can get it), redirects port 80 to HTTPS, and sets
  `X-Forwarded-Proto: https`;
- the backend is the container's address, port 5080, with *forwardfor* (X-Forwarded-For) on;
- set the backend's server timeout to 120 s or more: starting a stream waits for its first segment;
- optionally, health-check `GET /healthz` (200 while the server and its database answer).

[reverse-proxy.md](reverse-proxy.md) has the HAProxy configuration, and Apache or nginx in between. For a container
already set up, change `TrustedProxies` in `/etc/mediariffle/appsettings.json` ([Changing settings](#changing-settings)).

## Media from a file share

Mount the share on the **Proxmox node**, read-only, readable by everyone (an unprivileged container's users are
different numbers on the node), then pass the folder in. On the node:

```bash
apt install cifs-utils
install -m 0600 /dev/null /root/.smb-media
nano /root/.smb-media      # username=svc-mediariffle  password=…  domain=EXAMPLE
mkdir -p /mnt/media
echo '//fileserver/Media  /mnt/media  cifs  credentials=/root/.smb-media,ro,vers=3.1.1,file_mode=0644,dir_mode=0755,_netdev,nofail,x-systemd.automount  0  0' >> /etc/fstab
systemctl daemon-reload && mount /mnt/media && ls /mnt/media

pct set 120 -mp0 /mnt/media,mp=/mnt/media,ro=1,backup=0
pct reboot 120
```

The library paths are then `/mnt/media/Movies`, `/mnt/media/Shows` and so on. [file-server.md](file-server.md) covers
the account on the file server. MediaRiffle's service waits for `/mnt/media` at start (`RequiresMountsFor`); with the
media elsewhere, pass it in at `/mnt/media` anyway, or that line of the service needs an override
(`systemctl edit mediariffle`).

## Encoding and decoding on the GPU

To convert on the node's Intel or AMD GPU (an N100/N150 mini PC's, for one), pass its render node in, owned by the
container's `render` group (Proxmox 8.1 or newer):

```bash
pct exec 120 -- getent group render        # e.g. render:x:993:mediariffle
pct set 120 -dev0 /dev/dri/renderD128,gid=993
pct reboot 120
pct exec 120 -- vainfo --display drm --device /dev/dri/renderD128 | grep -E 'HEVC|H264'
```

`vainfo` should list `VAProfileH264…` with `VAEntrypointEncSlice` (or `EncSliceLP`). Then set `"Encoder": "Vaapi"` under
`Transcoding` ([Changing settings](#changing-settings)). MediaRiffle's log then says what the GPU decodes, e.g.
`Decoded on the GPU (Vaapi): H.264, HEVC, HEVC 10-bit, AV1, VP9, MPEG-2; HDR tone-mapped there too (libplacebo)`: video
is decoded, scaled, tone-mapped and encoded on the GPU (see Decoding on the GPU in
[configuration.md](configuration.md)). Older Intel GPUs encode with the `intel-media-va-driver-non-free` package
(Ubuntu's multiverse) when `vainfo` lists no `EncSlice`. NVIDIA needs its driver in the container too, which this
template doesn't include.

## Changing settings

Every setting is in `/etc/mediariffle/appsettings.json` ([configuration.md](configuration.md)):

```bash
pct exec 120 -- nano /etc/mediariffle/appsettings.json
pct exec 120 -- systemctl restart mediariffle
pct exec 120 -- journalctl -u mediariffle -n 30 --no-pager      # check it started
```

Running `/usr/local/sbin/mediariffle-firstboot` again applies `firstboot.env` once more (the port and proxies).

## Moving an existing install into the container

From another MediaRiffle (on Windows, or Ubuntu), everything that matters is in its data folder: the database
(accounts, passkeys, watch history, lists, libraries), the keys that sign sign-ins (so nobody has to sign in again)
and the pictures. The settings file is separate. Use the same release on both, or a newer one in the container.

1. **Create the container** as above, with the media and GPU passed in. Start it once, then stop MediaRiffle there:

   ```bash
   pct exec 120 -- systemctl stop mediariffle
   ```

2. **On the old server, stop MediaRiffle and pack its data.** On Windows (PowerShell, as an administrator):

   ```powershell
   Stop-Service MediaRiffle
   cd C:\ProgramData\MediaRiffle      # or DataDirectory, if your settings moved it
   $items = 'mediariffle.db', 'mediariffle.db-wal', 'mediariffle.db-shm', 'keys', 'images', 'avatars' | Where-Object { Test-Path $_ }
   tar -czf $HOME\mediariffle-data.tar.gz @items
   ```

   (On Ubuntu: `sudo systemctl stop mediariffle`, then
   `sudo tar -C /var/lib/mediariffle -czf mediariffle-data.tar.gz mediariffle.db keys images avatars`.) Note the
   settings you changed in `C:\ProgramData\MediaRiffle\appsettings.json` (or `/etc/mediariffle/appsettings.json`):
   Google sign-in, trusted proxies, quality limits.

3. **Copy it into the container**, through the node:

   ```bash
   scp you@oldserver:mediariffle-data.tar.gz /root/       # or copy it to the node any other way
   pct push 120 /root/mediariffle-data.tar.gz /root/mediariffle-data.tar.gz
   pct enter 120
   su -
   rm -rf /var/lib/mediariffle/*
   tar -xzf /root/mediariffle-data.tar.gz -C /var/lib/mediariffle
   chown -R mediariffle:mediariffle /var/lib/mediariffle
   ```

4. **Point the libraries at the container's paths.** Editing a library's folder under Admin → Libraries starts that
   library afresh (its watch history goes), so change them in the database instead, while MediaRiffle is stopped. The
   files inside keep their places as long as the folders under the share are the same:

   ```bash
   apt install -y sqlite3
   sqlite3 /var/lib/mediariffle/mediariffle.db 'SELECT Id, Name, Path FROM Libraries;'
   ```

   Then replace the share's part of the paths (`\\fileserver\Media` here: copy it exactly as the list shows it, as it's
   matched letter for letter) with where the share is in the container, and the rest of the backslashes with `/`:

   ```bash
   sqlite3 /var/lib/mediariffle/mediariffle.db <<'SQL'
   UPDATE Libraries SET Path = replace(replace(Path, '\\fileserver\Media', '/mnt/media'), '\', '/');
   SELECT Id, Name, Path FROM Libraries;
   SQL
   ```

   Every path should now start `/mnt/media/` (`\\fileserver\Media\TV Shows` → `/mnt/media/TV Shows`). One from
   another share comes out as `//server/...`: fix it with `UPDATE Libraries SET Path = '/mnt/other/Music' WHERE Id = 3;`.

5. **Settings:** put the ones you noted into `/etc/mediariffle/appsettings.json` (not `Kestrel`, `FfmpegFolder` or
   `DataDirectory`, which differ here; `Encoder` is `Vaapi` with the GPU passed in). Then start it and check:

   ```bash
   systemctl start mediariffle
   journalctl -u mediariffle -n 30 --no-pager
   ```

   Sign in on `http://<container>:5080/`; Admin → Libraries → Scan now checks every library still finds its files.

6. **Switch HAProxy's backend** to the container's address and port 5080. With the same public name, Google sign-in
   and passkeys carry on working (they're tied to the name, not the server). Keep the old server stopped (or its
   service disabled) until you're happy, then remove it.

### Lists, favorites and watch history that didn't come across

If a library was started afresh in the container (its folder changed under Admin → Libraries, or the library added
again), its shows, movies and files were scanned with new ids, and the lists, favorites, hidden items and watch
progress that pointed at the old ones went with them. They can be brought back from the old database, matched by what
things are (users by user name; shows, movies, albums, artists and files by their library's name and their folder or
path in it), so the ids don't matter. What the new libraries don't have is left out, and nothing is added twice.

1. **Export, on the old server**, with `deploy/export-user-data.sql`. It only reads the old database and makes an import
   script. In DB Browser for SQLite: **Open Database** → `C:\ProgramData\MediaRiffle\mediariffle.db` (MediaRiffle
   stopped, or a copy of it); **Execute SQL** → open `export-user-data.sql` → **Execute all** (F5). The result is one
   cell: click it, and in the **Edit Database Cell** pane, **Export to file** → `import-user-data.sql`. Its first lines
   say what's in it, e.g. `from 3 users: 22 list entries, 1 hidden, 266 watch progress`.

2. **Import, in the container**, its libraries scanned:

   ```bash
   # on the node
   pct push 120 import-user-data.sql /root/import-user-data.sql
   pct enter 120
   su -
   apt install -y sqlite3
   systemctl stop mediariffle
   cp /var/lib/mediariffle/mediariffle.db /root/mediariffle.db.before-import
   sqlite3 -bail /var/lib/mediariffle/mediariffle.db < /root/import-user-data.sql
   chown mediariffle:mediariffle /var/lib/mediariffle/mediariffle.db*
   systemctl start mediariffle
   ```

   It ends with `Added or updated N of M`: fewer than M when some aren't in the new libraries (not scanned yet, or
   gone) or were there already. Running it again adds nothing twice, and watch progress already in the new database is
   kept unless the old one is newer. A user with a different user name in the new database is left out.
   `/root/mediariffle.db.before-import` is the database as it was, if anything looks wrong.

With both databases on one machine, `deploy/carry-user-data.sql` does the same in one step: open the new database,
attach the old one as `old`, and run it (the instructions are at the top of the file).

## Upgrading to a new release

From the release after 0.8.0, **Admin → Updates** does this for you: it downloads the new `.deb`, checks it, installs
it and goes back to the old version if the new one doesn't start ([updating](updating.md)). Take a snapshot first
(step 1 below) all the same. To install a release by hand, or the first one with Admin → Updates:

A container is upgraded in place with the release's `.deb`, as any Ubuntu machine is. MediaRiffle's data
(`/var/lib/mediariffle`) and settings (`/etc/mediariffle/appsettings.json`) are kept; the program in
`/opt/mediariffle` is replaced, and the database is brought up to date when it starts.

1. **On the node, save a copy first:**

   ```bash
   pct snapshot 120 before-upgrade
   ```

   If the container's storage can't snapshot, back it up instead (the media mount point, `backup=0`, is left out):

   ```bash
   vzdump 120 --mode snapshot --storage local
   ```

2. **Open a login shell in the container.** Type these one at a time and wait for each prompt (pasted together, the
   second is lost while the shell starts):

   ```bash
   pct enter 120
   su -
   ```

3. **Install the new release** (set `VER` to it):

   ```bash
   VER=0.7.1
   cd /root
   wget -q "https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/releases/v$VER/downloads/mediariffle_$VER-1_amd64.deb"
   apt update
   apt install ./mediariffle_$VER-1_amd64.deb
   ```

   apt stops MediaRiffle, replaces it and starts it again. If it asks about `/etc/mediariffle/appsettings.json`, keep
   yours (the default, **N**): the new release's own is beside it as `appsettings.json.dpkg-dist`, to compare.

4. **Check it:**

   ```bash
   systemctl is-active mediariffle
   journalctl -u mediariffle -n 30 --no-pager
   curl -s http://127.0.0.1:5080/api/auth/state      # "version":"0.7.1"
   ```

   Then sign in and play something. Once you're happy, remove the download (`rm /root/mediariffle_*.deb`) and the
   snapshot (`pct delsnapshot 120 before-upgrade` on the node).

**Rolling back:** `pct rollback 120 before-upgrade` (or restore the vzdump backup) returns the container, data
included, to the moment of the copy. Installing the older `.deb` alone isn't enough once the new release has updated
the database.

Read the release's CHANGELOG entry before upgrading: anything that needs doing by hand is noted there.

**Ubuntu and ffmpeg** are kept up to date like any Ubuntu machine's: `apt update && apt upgrade` in the container (a
snapshot first is cheap insurance), or turn on `unattended-upgrades` for security updates.

## Notes

- `pct enter` and `pct exec` start with a short PATH that leaves out `/usr/local/sbin`, where `mediariffle-firstboot`
  lives. Run `su -` after `pct enter` to get a normal login shell, or call it by its full path.
- Logs: `journalctl -u mediariffle` (MediaRiffle), `journalctl -u mediariffle-firstboot` (the first boot).
- Unprivileged containers with nesting are fine. If MediaRiffle won't start with `status=226/NAMESPACE`, nesting is
  off: `pct set 120 --features nesting=1` and restart the container.
- The template is built by `proxmox/build-template.sh`, which installs the `.deb` into Proxmox's `ubuntu-24.04-standard`
  template (checked against its published SHA-512) in a chroot and packs it again with no machine ID, SSH host keys or
  MediaRiffle data. `proxmox/test-template.sh` boots the result with Docker standing in for `pct` and checks MediaRiffle
  answers with the first-boot settings applied. Both run in the release pipelines (GitLab: `release-proxmox` on the
  Linux runner; GitHub: the `proxmox` job), or by hand as root:

  ```bash
  sudo proxmox/build-template.sh mediariffle_<version>-1_amd64.deb out/MediaRiffle-v<version>-proxmox-amd64.tar.zst
  sudo proxmox/test-template.sh out/MediaRiffle-v<version>-proxmox-amd64.tar.zst
  ```
