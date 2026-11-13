#!/usr/bin/env bash
# Eseguito DENTRO un container Debian/Ubuntu da scripts/test-linux-deb-gui.ps1.
# Uso: test-linux-lint.sh <file.deb>
# Struttura del .deb (dpkg-deb), file .desktop (desktop-file-validate) e lintian: fallisce sugli errori («E:»).
set -u
DEB=$1
export DEBIAN_FRONTEND=noninteractive
. /etc/os-release
echo "== lintian su $PRETTY_NAME"
apt-get update -qq > /dev/null 2>&1 || { echo "FALLITO: apt-get update"; exit 1; }
apt-get install -y -qq --no-install-recommends lintian desktop-file-utils shared-mime-info > /tmp/apt-lint.log 2>&1 \
  || { tail -15 /tmp/apt-lint.log; echo "FALLITO: installazione di lintian"; exit 1; }

dpkg-deb -I "$DEB" | sed -n '1,40p'
echo "Compressione dei membri: $(ar t "$DEB" | tr '\n' ' ')"
case $(ar t "$DEB") in *zst*) echo "FALLITO: il .deb usa zstd, i gestori vecchi non lo leggono (usare xz)"; exit 1;; esac
echo "Proprietario dei file: $(dpkg-deb -c "$DEB" | awk '{print $2}' | sort -u | tr '\n' ' ')"
dpkg-deb -c "$DEB" | awk '{print $2}' | sort -u | grep -qvx 'root/root' && { echo "FALLITO: file non di proprietà root/root"; exit 1; }

EXT=/tmp/ext; mkdir -p "$EXT"; dpkg-deb -x "$DEB" "$EXT"
desktop-file-validate "$EXT/usr/share/applications/rdpmanager.desktop" || { echo "FALLITO: desktop-file-validate"; exit 1; }
echo "desktop-file-validate: ok"

lintian --no-cfg --display-level '>=warning' --fail-on error "$DEB" 2>&1 | tee /tmp/lintian.log
RC=${PIPESTATUS[0]}
if [[ $RC -ne 0 ]] || grep -q '^E:' /tmp/lintian.log; then echo "FALLITO: lintian segnala errori"; exit 1; fi
echo "lintian: nessun errore"
echo "OK: struttura e lintian"
