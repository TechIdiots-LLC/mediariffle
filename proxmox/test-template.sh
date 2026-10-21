#!/bin/bash
# Tests a MediaRiffle Proxmox template. Exits non-zero on failure.
#
# usage (as root): proxmox/test-template.sh TEMPLATE.tar.zst
#
# With Docker (a GitHub runner, a privileged GitLab runner): boots the template the way a new container starts, with
# Docker standing in for pct and a firstboot.env as an admin would write it, and checks MediaRiffle answers with the
# settings applied. Without Docker (an ordinary GitLab Docker-executor job): checks the template's files and runs
# mediariffle-firstboot in a chroot of it.
set -euo pipefail
[ $# -eq 1 ] || { echo "usage: $0 TEMPLATE.tar.zst" >&2; exit 2; }
template=$(readlink -f "$1")
log() { echo "[$(date +%H:%M:%S)] $*"; }
fail() { log "FAILED: $*"; exit 1; }

env_file=$(mktemp)
cat > "$env_file" <<'ENV'
MEDIARIFFLE_PORT=8096
MEDIARIFFLE_TRUSTED_PROXIES="192.168.0.1, 192.168.0.2 10.0.0.0/8"
MEDIARIFFLE_FORWARD_LIMIT=2
ENV
settings_ok() {   # $1: the settings file's text
    grep -q '"Url": "http://\*:8096"' <<< "$1" || fail "the port wasn't applied"
    grep -q '"TrustedProxies": \[ "127.0.0.1", "::1", "192.168.0.1", "192.168.0.2", "10.0.0.0/8" \]' <<< "$1" ||
        fail "the trusted proxies weren't applied"
    grep -q '"ForwardLimit": 2' <<< "$1" || fail "the forward limit wasn't applied"
}

if command -v docker >/dev/null && docker info >/dev/null 2>&1; then
    name=mediariffle-template-test-$$
    image=mediariffle-template-test:$$
    cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; docker rmi "$image" >/dev/null 2>&1 || true; rm -f "$env_file"; }
    trap cleanup EXIT

    log "importing the template"
    zstd -dc --long=27 "$template" | docker import - "$image" >/dev/null
    docker create --name "$name" --hostname mediariffle --privileged --cgroupns=private \
        --tmpfs /run --tmpfs /run/lock "$image" /sbin/init >/dev/null
    docker cp "$env_file" "$name:/etc/mediariffle/firstboot.env"
    log "booting"
    docker start "$name" >/dev/null
    code=""
    for i in $(seq 90); do
        code=$(docker exec "$name" curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:8096/api/auth/state 2>/dev/null || true)
        [ "$code" = 200 ] && break
        if docker exec "$name" systemctl is-failed -q mediariffle-firstboot mediariffle 2>/dev/null; then break; fi
        sleep 2
    done
    if [ "$code" != 200 ]; then
        docker exec "$name" journalctl -u mediariffle-firstboot -u mediariffle --no-pager | tail -60 || true
        fail "MediaRiffle didn't answer on port 8096 (last: ${code:-none})"
    fi
    state=$(docker exec "$name" curl -s http://127.0.0.1:8096/api/auth/state)
    log "MediaRiffle answers: $state"
    grep -q '"setupNeeded":true' <<< "$state" || fail "a new container should ask for its first admin account"
    settings_ok "$(docker exec "$name" cat /etc/mediariffle/appsettings.json)"
    docker exec "$name" test ! -e /etc/mediariffle/firstboot-pending || fail "firstboot-pending is still there"
    docker exec "$name" test -s /root/mediariffle-first-login.txt || fail "no first-login note"
    docker exec "$name" sh -c 'test -s /etc/machine-id' || fail "no machine ID"
    docker exec "$name" sh -c 'ls /etc/ssh/ssh_host_*_key >/dev/null' || fail "no SSH host keys"
    docker exec "$name" test -f /var/lib/mediariffle/mediariffle.db || fail "MediaRiffle made no database"
    docker exec "$name" sh -c 'ffmpeg -hide_banner -version | head -1; ffmpeg -hide_banner -encoders 2>/dev/null | grep -E "h264_(qsv|vaapi|nvenc)"'
    log "template boot test passed"
    exit 0
fi

log "no Docker here: checking the template's files instead of booting it"
[ "$(id -u)" = 0 ] || fail "run as root (to unpack the template with its owners)"
work=$(mktemp -d)
trap 'rm -rf "$work" "$env_file"' EXIT
root=$work/rootfs
mkdir -p "$root"
zstd -dc --long=27 "$template" | tar -x -C "$root" --numeric-owner -p
test -x "$root/opt/mediariffle/MediaRiffle.Server" || fail "no MediaRiffle program"
test -x "$root/usr/bin/ffmpeg" || fail "no ffmpeg"
test -L "$root/etc/systemd/system/multi-user.target.wants/mediariffle.service" || fail "the service isn't enabled"
test -L "$root/etc/systemd/system/multi-user.target.wants/mediariffle-firstboot.service" || fail "first boot isn't armed"
test -f "$root/etc/mediariffle/firstboot-pending" || fail "first boot isn't armed"
grep -q '"Url": "http://\*:5080"' "$root/etc/mediariffle/appsettings.json" || fail "it doesn't listen on the container's network"
test ! -s "$root/etc/machine-id" || fail "the template has a machine ID"
! ls "$root"/etc/ssh/ssh_host_*_key >/dev/null 2>&1 || fail "the template has SSH host keys"
[ -z "$(ls -A "$root/var/lib/mediariffle")" ] || fail "the template has MediaRiffle data"
[ "$(stat -c %U "$root/var/lib/mediariffle" 2>/dev/null || true)" != root ] || fail "the data folder belongs to root"
[ -z "$(ls -A "$root/dev")" ] || fail "the template has device nodes"
log "running mediariffle-firstboot in a chroot"
cp "$env_file" "$root/etc/mediariffle/firstboot.env"
chroot "$root" /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin /usr/local/sbin/mediariffle-firstboot
settings_ok "$(cat "$root/etc/mediariffle/appsettings.json")"
test ! -e "$root/etc/mediariffle/firstboot-pending" || fail "firstboot-pending is still there"
log "template file test passed"
