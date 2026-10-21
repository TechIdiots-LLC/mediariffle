# Running MediaRiffle in a Proxmox container

Each release has `MediaRiffle-v<version>-proxmox-amd64.tar.zst`, an LXC template: Proxmox's own Ubuntu 24.04 template
with MediaRiffle's `.deb` installed, ffmpeg with it, and the VA-API drivers for Intel or AMD encoding. It listens on
port 5080 of the container's network and makes its database and keys when it first starts, so every container made
from it is its own.

Host names and addresses here are placeholders.

## 1. Put the template on the Proxmox host

In the web interface: a storage that holds container templates (e.g. **local**) → **CT Templates** → **Upload**, and
pick the file. Or on the host:

```sh
cd /var/lib/vz/template/cache
wget https://gitlab.techidiots.net/techidiots-llc/mediariffle/-/releases/v0.7.0/downloads/MediaRiffle-v0.7.0-proxmox-amd64.tar.zst
```

## 2. Create the container

**Create CT** with the template, or on the host:

```sh
pct create 120 local:vztmpl/MediaRiffle-v0.7.0-proxmox-amd64.tar.zst \
  --hostname mediariffle --ostype ubuntu --unprivileged 1 --features nesting=1 \
  --cores 4 --memory 4096 --swap 1024 --rootfs local-lvm:16 \
  --net0 name=eth0,bridge=vmbr0,ip=dhcp --onboot 1
```

- **Unprivileged, with nesting on** (the default for a new container in the web interface): MediaRiffle's service is
  sandboxed by systemd, which needs nesting in an unprivileged container.
- **Cores**: transcoding uses all it gets. 4 is enough for a 1080p transcode or two; a 4K HDR source wants more, or a
  GPU (below). Playing files as they are hardly uses any.
- **Disk**: the program and Ubuntu take about 2 GB; transcodes are written to `/var/lib/mediariffle/transcode` while
  they play and deleted after, about 1 GB an hour of 1080p each. 16 GB is plenty.

Don't start it yet if you want to set things up first (step 3).

## 3. First boot settings (optional)

Before the first start, MediaRiffle can be told its port and the reverse proxies in front of it, in
`/etc/mediariffle/firstboot.env`:

```sh
pct mount 120
nano /var/lib/lxc/120/rootfs/etc/mediariffle/firstboot.env
pct unmount 120
```

```sh
MEDIARIFFLE_PORT=5080
# e.g. HAProxy on OPNsense: both firewalls and their CARP address
MEDIARIFFLE_TRUSTED_PROXIES="192.168.0.1 192.168.0.2 192.168.0.3"
MEDIARIFFLE_FORWARD_LIMIT=1
```

They go into `/etc/mediariffle/appsettings.json` when the container first starts. That file is where every setting
lives ([configuration.md](configuration.md)); to change one later, edit it and `systemctl restart mediariffle`.

## 4. Start it and make the admin account

```sh
pct start 120
pct exec 120 -- cat /root/mediariffle-first-login.txt
```

That lists the addresses it answers on, e.g. `http://192.168.0.120:5080/`. **The first visit creates the admin
account**, so open it straight away, before anyone else on the network can. Then put your reverse proxy in front for
HTTPS ([reverse-proxy.md](reverse-proxy.md)), pointing it at the container's address and port 5080.

## 5. Media

A container can't mount a Windows or Samba share itself unless it's privileged, so mount the share on the Proxmox
host and pass the folder in:

```sh
# on the host: /etc/fstab (see file-server.md for the credentials file)
//fileserver/media  /mnt/media  cifs  credentials=/root/.smb-media,ro,file_mode=0644,dir_mode=0755,_netdev,nofail  0 0

pct set 120 -mp0 /mnt/media,mp=/mnt/media,ro=1
pct reboot 120
```

The files must be readable by everyone (`file_mode=0644,dir_mode=0755`): an unprivileged container's users are
different numbers on the host. Then add `/mnt/media/Movies` and the rest under **Admin → Libraries**. The service waits
for `/mnt/media` at start (`RequiresMountsFor`), so pass the media in there or edit that line of the service.

## 6. Hardware encoding (optional)

To encode on the host's Intel or AMD GPU, pass its render node in (Proxmox 8.1 or newer), owned by the container's
`render` group:

```sh
pct exec 120 -- getent group render        # e.g. render:x:993:mediariffle
pct set 120 -dev0 /dev/dri/renderD128,gid=993
pct reboot 120
pct exec 120 -- vainfo --display drm --device /dev/dri/renderD128
```

Then set `"Encoder": "Vaapi"` under `Transcoding` in `/etc/mediariffle/appsettings.json`
and restart MediaRiffle. The log then lists what the GPU decodes: with `Vaapi` it decodes, scales, tone-maps HDR (with
libplacebo, through Vulkan) and encodes, all on the GPU, which is what lets a small mini PC convert 4K (see Decoding on
the GPU in [configuration.md](configuration.md)). NVIDIA needs its driver in the container too, which this template
doesn't include.

## Upgrading

Inside the container, install the new release's `.deb` the same way as on any Ubuntu machine
([install-ubuntu.md](install-ubuntu.md)):

```sh
apt update && apt install ./mediariffle_0.7.1-1_amd64.deb
```

Your data and settings stay. If apt asks about `/etc/mediariffle/appsettings.json`, keep yours (the default, **N**).
`apt upgrade` keeps Ubuntu and ffmpeg up to date.

## How the template is made

`proxmox/build-template.sh` unpacks Proxmox's `ubuntu-24.04-standard` template (checked against its published SHA-512),
installs the `.deb` in a chroot, sets it to listen on the container's network, arms `mediariffle-firstboot` and packs
it again with no machine ID, SSH host keys or MediaRiffle data. `proxmox/test-template.sh` boots the result with Docker
standing in for `pct` and checks MediaRiffle answers with the first boot settings applied. Both run in the release
pipelines (GitLab: `release-proxmox` on the Linux runner; GitHub: the `proxmox` job), or by hand, as root:

```sh
sudo proxmox/build-template.sh mediariffle_0.7.0-1_amd64.deb out/MediaRiffle-v0.7.0-proxmox-amd64.tar.zst
sudo proxmox/test-template.sh out/MediaRiffle-v0.7.0-proxmox-amd64.tar.zst
```
