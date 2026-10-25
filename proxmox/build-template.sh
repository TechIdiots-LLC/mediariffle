#!/bin/bash
# Builds the MediaRiffle Proxmox LXC template: Proxmox's own Ubuntu 24.04 template with MediaRiffle's .deb installed
# (and the ffmpeg it brings), set up to listen on the container's network, ready for its first boot.
#
# usage (as root, on Linux): proxmox/build-template.sh MEDIARIFFLE.deb OUTPUT.tar.zst [BASE.tar.zst]
#
# BASE is Proxmox's ubuntu-24.04-standard template; without it, it's downloaded and checked against the SHA-512 in
# Proxmox's template index (aplinfo). Needs tar, zstd and curl.
#
# How: unpack the base, chroot into it, bring it up to date and install the .deb (its postinst sees no running
# systemd, so it only creates the service user and data folder; the service is enabled here instead), then reset what
# must differ per container (machine ID, SSH host keys), arm mediariffle-firstboot and pack the filesystem. No Docker
# and no systemd: this runs as root in an ordinary CI container too (a GitLab Docker-executor job, a GitHub runner).
# /proc and /sys are mounted for apt when allowed (not in an unprivileged container, where apt manages without).
set -euo pipefail

BASE_URL=http://download.proxmox.com/images/system/ubuntu-24.04-standard_24.04-2_amd64.tar.zst
BASE_SHA512=45c2978e6b97fe292ada95fe06834276015e5739a594db4de2fdfd830fa0c37942e8ae118fc1e32ffd9154b3f9378b592738b668ea3957db41f2907b86f219de

[ $# -ge 2 ] || { echo "usage: $0 MEDIARIFFLE.deb OUTPUT.tar.zst [BASE.tar.zst]" >&2; exit 2; }
[ "$(id -u)" = 0 ] || { echo "$0: run as root" >&2; exit 1; }
deb=$(readlink -f "$1")
# -m: the output's folder may not exist yet
output=$(readlink -m "$2")
base=${3:+$(readlink -f "$3")}
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
root=$work/rootfs
mounted=()

log() { echo "[$(date +%H:%M:%S)] $*"; }
cleanup() {
    for ((i = ${#mounted[@]} - 1; i >= 0; i--)); do umount -l "${mounted[i]}" 2>/dev/null || true; done
    rm -rf "$work"
}
trap cleanup EXIT
in_root() { chroot "$root" /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin HOME=/root LANG=C.UTF-8 \
    DEBIAN_FRONTEND=noninteractive "$@"; }

if [ -z "$base" ]; then
    log "downloading Proxmox's Ubuntu 24.04 template"
    base=$work/base.tar.zst
    curl -fsSL --retry 3 -o "$base" "$BASE_URL"
fi
echo "$BASE_SHA512  $base" | sha512sum -c --quiet - || { log "the base template's checksum doesn't match"; exit 1; }

log "unpacking it"
mkdir -p "$root"
zstd -dc "$base" | tar -x -C "$root" --numeric-owner -p --xattrs --xattrs-include='*'

# What apt needs: /proc and /sys (when the build may mount them), a few device nodes, DNS, and services kept from
# starting (there's no init in a chroot)
for fs in proc sys; do
    if mount -t "$fs" "$fs" "$root/$fs" 2>/dev/null; then mounted+=("$root/$fs"); fi
done
[ ${#mounted[@]} -gt 0 ] || log "(not allowed to mount /proc here: installing without it)"
made_dev=()
for node in null:1:3 zero:1:5 random:1:8 urandom:1:9; do
    IFS=: read -r name major minor <<< "$node"
    if [ ! -e "$root/dev/$name" ]; then mknod -m 666 "$root/dev/$name" c "$major" "$minor"; made_dev+=("$name"); fi
done
resolv=$root/etc/resolv.conf
resolv_link=$(readlink "$resolv" 2>/dev/null || true)
mv "$resolv" "$work/resolv.conf.orig" 2>/dev/null || true
cp -L /etc/resolv.conf "$resolv"
printf '#!/bin/sh\nexit 101\n' > "$root/usr/sbin/policy-rc.d"
chmod 0755 "$root/usr/sbin/policy-rc.d"

log "updating Ubuntu and installing MediaRiffle"
# ffmpeg is in universe
grep -rqs universe "$root/etc/apt/sources.list" "$root/etc/apt/sources.list.d" ||
    { log "the base template has no universe repository"; exit 1; }
cp "$deb" "$root/tmp/mediariffle.deb"
in_root apt-get update -q
in_root apt-get -y -q -o Dpkg::Options::=--force-confold dist-upgrade
# For the host's Intel or AMD GPU, when it's passed through (docs/proxmox.md): VA-API drivers to encode on it, Mesa's
# Vulkan driver for libplacebo to tone-map HDR and Dolby Vision on it. curl: for checking it answers
in_root apt-get install -y -q --no-install-recommends /tmp/mediariffle.deb \
    mesa-va-drivers intel-media-va-driver vainfo mesa-vulkan-drivers curl
rm -f "$root/tmp/mediariffle.deb"

log "setting it up for a container"
# Listen on the container's network (the reverse proxy is another machine), port 5080; mediariffle-firstboot changes
# the port and trusted proxies from /etc/mediariffle/firstboot.env
settings=$root/etc/mediariffle/appsettings.json
sed -i 's|"Url": "http://127.0.0.1:5080"|"Url": "http://*:5080"|' "$settings"
sed -i 's|// 127.0.0.1 when the proxy runs on this machine.*|// Every address of the container (the reverse proxy is another machine); allow only the proxy to reach the port|' "$settings"
sed -i '/^    \/\/ (then allow only the proxy to reach the port)$/d' "$settings"
grep -q '"Url": "http://\*:5080"' "$settings" || { log "couldn't set the listen address in $settings"; exit 1; }

install -m 0755 "$here/mediariffle-firstboot" "$root/usr/local/sbin/mediariffle-firstboot"
install -m 0644 "$here/mediariffle-firstboot.service" "$root/etc/systemd/system/mediariffle-firstboot.service"
install -m 0644 "$here/firstboot.env" "$root/etc/mediariffle/firstboot.env"
touch "$root/etc/mediariffle/firstboot-pending"
# Enabled without a running systemd (systemctl works on the files in a chroot)
in_root systemctl enable mediariffle.service mediariffle-firstboot.service mediariffle-update.path

cat > "$root/etc/motd" <<'MOTD'

  MediaRiffle: open http://<this container's address>:5080/ (or the address your reverse proxy gives it); the
  first visit creates the admin account. Settings: /etc/mediariffle/appsettings.json, then
  systemctl restart mediariffle. Logs: journalctl -u mediariffle. See docs/proxmox.md in the repository.

MOTD

log "cleaning up"
rm -f "$root/usr/sbin/policy-rc.d"
in_root apt-get clean
rm -rf "$root"/var/lib/apt/lists/* "$root"/tmp/* "$root"/var/tmp/* "$root/root/.bash_history"
# Per-container identity: a new machine ID at first boot, SSH host keys made by pct (or by mediariffle-firstboot)
: > "$root/etc/machine-id"
rm -f "$root/var/lib/dbus/machine-id" "$root"/etc/ssh/ssh_host_*
# Nothing of MediaRiffle's own yet: the database and keys are made when the container first starts
find "$root/var/lib/mediariffle" -mindepth 1 -delete
find "$root/var/log" -type f \( -name '*.gz' -o -name '*.[0-9]' \) -delete
find "$root/var/log" -type f -exec truncate -s 0 {} +
for name in "${made_dev[@]}"; do rm -f "$root/dev/$name"; done
rm -f "$resolv"
if [ -n "$resolv_link" ]; then ln -s "$resolv_link" "$resolv"
elif [ -e "$work/resolv.conf.orig" ]; then mv "$work/resolv.conf.orig" "$resolv"; fi
for ((i = ${#mounted[@]} - 1; i >= 0; i--)); do umount -l "${mounted[i]}"; done
mounted=()

log "packing"
mkdir -p "$(dirname "$output")"
tar -C "$root" --numeric-owner --xattrs --xattrs-include='*' -cf - . | zstd -q -T0 -19 --long=27 -o "$output" -f
# check the archive reads back
zstd -dc --long=27 "$output" | tar -t >/dev/null
log "template: $output ($(du -h "$output" | cut -f1))"
