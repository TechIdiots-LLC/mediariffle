# Installing on Ubuntu 24.04

MediaRiffle runs as a systemd service under its own unprivileged user, listening on a local port behind HAProxy (or
Apache/nginx), which handles HTTPS. See [reverse-proxy.md](reverse-proxy.md) for that part and
[file-server.md](file-server.md) for mounting media from another server.

Host names here are placeholders (`media01.example.lan`, `media.example.com`).

## The quick way: the .deb

Download `mediariffle_<version>-1_amd64.deb` (or `_arm64.deb`) from the release, then:

```sh
sudo apt install ./mediariffle_0.1.0-1_amd64.deb
```

That installs ffmpeg and the libraries .NET needs, and sets everything up:

- the program in `/opt/mediariffle`
- the `mediariffle` service user, and `/var/lib/mediariffle` for its data
- your settings in `/etc/mediariffle/appsettings.json`
- the hardened `mediariffle` systemd service, enabled and started

Edit the settings (step 4 below explains them), then `sudo systemctl restart mediariffle`.

- **Upgrading** is the same `apt install` with the newer `.deb`: the service is stopped, replaced and started again,
  and your settings are kept. If both you and the new version changed the settings file, apt asks which to keep.
- **`sudo apt remove mediariffle`** removes the program but keeps the data and settings.
- **`sudo apt purge mediariffle`** also deletes the data, the settings and the service user.

For another systemd distribution, use `MediaRiffle-v<version>-linux-x64.tar.gz` instead: unpack it and run
`sudo sh install.sh`, which does the same (and `sudo sh install.sh --uninstall` to remove it).

To mount a file share for the media, see [file-server.md](file-server.md). The steps below are the same setup done by
hand.

## 1. Packages

```sh
sudo apt update
sudo apt install ffmpeg cifs-utils
ffmpeg -version | head -1      # Ubuntu 24.04 ships FFmpeg 6.1
```

For hardware encoding:

- Intel (Quick Sync / VA-API): `sudo apt install intel-media-va-driver-non-free vainfo`, then `vainfo` should list
  `VAProfileH264*` with `VAEntrypointEncSlice`.
- AMD (VA-API): `sudo apt install mesa-va-drivers vainfo`.
- NVIDIA (NVENC): the proprietary driver (`ubuntu-drivers install`); Ubuntu's ffmpeg includes `h264_nvenc`.

The program is published self-contained, so the .NET runtime doesn't need installing.

## 2. Service user and folders

```sh
sudo useradd --system --home-dir /var/lib/mediariffle --shell /usr/sbin/nologin mediariffle
sudo usermod -aG render mediariffle            # only needed for VA-API / Quick Sync
sudo install -d -o mediariffle -g mediariffle -m 0750 /var/lib/mediariffle
sudo install -d -m 0755 /opt/mediariffle /etc/mediariffle
```

## 3. Build and copy the program

On a build machine (Windows or Linux) with the .NET 10 SDK:

```sh
dotnet publish src/MediaRiffle.Server -c Release -r linux-x64 --self-contained -o publish/linux-x64
```

Copy `publish/linux-x64/` to `/opt/mediariffle/` on the server, then:

```sh
sudo chown -R root:root /opt/mediariffle
sudo chmod 0755 /opt/mediariffle/MediaRiffle.Server
```

The program folder is owned by root and read-only to the service, so a compromised service can't change itself.

## 4. Settings

```sh
sudoedit /etc/mediariffle/appsettings.json
sudo chown root:mediariffle /etc/mediariffle/appsettings.json
sudo chmod 0640 /etc/mediariffle/appsettings.json
```

```json
{
  "MediaRiffle": {
    "TrustedProxies": [ "10.0.0.5" ],
    "ForwardLimit": 1,
    "Libraries": [
      { "Name": "TV Shows", "Kind": "Shows",  "Path": "/mnt/media/Shows" },
      { "Name": "Movies",   "Kind": "Movies", "Path": "/mnt/media/Movies" }
    ],
    "Transcoding": { "Encoder": "Libx264" }
  },
  "Kestrel": {
    "Endpoints": { "Http": { "Url": "http://10.0.0.20:5080" } }
  }
}
```

- `Libraries` here only adds the first libraries on the first start; you can also leave it out and add them in the
  browser under **Admin → Libraries**, which is where they're managed from then on.
- `10.0.0.5` is HAProxy's address and `10.0.0.20` this server's. If the proxy runs on this machine, use `127.0.0.1` for
  both.
- Every setting is described in [configuration.md](configuration.md).

When MediaRiffle listens on a LAN address, allow only the proxy to reach it:

```sh
sudo ufw allow from 10.0.0.5 to any port 5080 proto tcp
```

## 5. Start it

```sh
sudo cp deploy/linux/mediariffle.service /etc/systemd/system/   # from the repository
sudo systemctl daemon-reload
sudo systemctl enable --now mediariffle
systemctl status mediariffle
journalctl -u mediariffle -f
```

The log shows the ffmpeg version found and each library scan. Then open `https://media.example.com/` through the proxy;
the first visit asks you to create the admin account. Invite everyone else from **Admin**.

## Upgrading

Stop the service, replace `/opt/mediariffle` with the new publish output, start it again. The database is upgraded on
start; settings in `/etc/mediariffle` and data in `/var/lib/mediariffle` are kept.

## Backups

`/var/lib/mediariffle/mediariffle.db` (accounts and watch positions) and `/var/lib/mediariffle/keys` (without the keys,
everyone has to sign in again). `images/` and `transcode/` are rebuilt on their own.
