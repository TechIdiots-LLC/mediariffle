#!/bin/sh
# Installs (or upgrades) MediaRiffle from this folder on a systemd Linux that isn't Debian/Ubuntu (for those, use the
# .deb). Run as root:  sudo sh install.sh        Remove:  sudo sh install.sh --uninstall   (keeps the data)
set -e
here="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "Run as root: sudo sh $0" >&2; exit 1; fi

if [ "$1" = "--uninstall" ]; then
    systemctl disable --now mediariffle.service 2>/dev/null || true
    rm -f /etc/systemd/system/mediariffle.service
    systemctl daemon-reload
    rm -rf /opt/mediariffle
    echo "Removed. Data in /var/lib/mediariffle and settings in /etc/mediariffle are kept; delete them to remove everything."
    exit 0
fi

command -v ffmpeg >/dev/null 2>&1 || echo "Warning: ffmpeg isn't installed; install it with your package manager." >&2

if ! id mediariffle >/dev/null 2>&1; then
    useradd --system --home-dir /var/lib/mediariffle --no-create-home --shell /usr/sbin/nologin mediariffle
fi
getent group render >/dev/null && usermod -aG render mediariffle || true
install -d -o mediariffle -g mediariffle -m 0750 /var/lib/mediariffle
install -d -m 0755 /etc/mediariffle

systemctl stop mediariffle.service 2>/dev/null || true
rm -rf /opt/mediariffle
mkdir -p /opt/mediariffle
cp -R "$here/app/." /opt/mediariffle/
chown -R root:root /opt/mediariffle
chmod -R u=rwX,go=rX /opt/mediariffle
chmod 0755 /opt/mediariffle/MediaRiffle.Server
[ -f /opt/mediariffle/createdump ] && chmod 0755 /opt/mediariffle/createdump

if [ ! -f /etc/mediariffle/appsettings.json ]; then
    install -m 0640 -o root -g mediariffle "$here/appsettings.json" /etc/mediariffle/appsettings.json
fi
install -m 0644 "$here/mediariffle.service" /etc/systemd/system/mediariffle.service
systemctl daemon-reload
systemctl enable --now mediariffle.service
echo "MediaRiffle is running; settings are in /etc/mediariffle/appsettings.json. See: journalctl -u mediariffle -f"
