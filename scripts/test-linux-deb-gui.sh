#!/usr/bin/env bash
# Eseguito DENTRO un sistema Debian/Ubuntu pulito da scripts/test-linux-deb-gui.ps1.
# Uso: test-linux-deb-gui.sh <file.deb>
# Installa il .deb come fa il gestore grafico (App Center, GNOME Software, Mint): attraverso PackageKit
# (`pkcon install-local`, con packagekitd e bus di sistema avviati); se PackageKit nel container non regge,
# ripiega su gdebi (usato da Mint/aptdaemon). Poi controlla file, librerie native e rimozione.
set -u
DEB=$1
export DEBIAN_FRONTEND=noninteractive
. /etc/os-release
echo "== $PRETTY_NAME"

apt-get update -qq > /dev/null 2>&1 || { echo "FALLITO: apt-get update"; exit 1; }
apt-get install -y -qq --no-install-recommends packagekit packagekit-tools dbus gdebi-core policykit-1 > /tmp/apt-tools.log 2>&1 \
  || apt-get install -y -qq --no-install-recommends packagekit packagekit-tools dbus gdebi-core polkitd > /tmp/apt-tools.log 2>&1 \
  || { tail -15 /tmp/apt-tools.log; echo "FALLITO: installazione di PackageKit"; exit 1; }
cp "$DEB" /tmp/rdpmanager.deb
chmod 644 /tmp/rdpmanager.deb

# Il bus di sistema e packagekitd (in un container non c'è systemd).
mkdir -p /run/dbus
rm -f /run/dbus/pid
dbus-daemon --system --fork 2>/dev/null || true
for i in 1 2 3 4 5; do [[ -S /run/dbus/system_bus_socket ]] && break; sleep 1; done
for p in /usr/libexec/packagekitd /usr/lib/packagekit/packagekitd; do
  [[ -x $p ]] && { "$p" > /tmp/packagekitd.log 2>&1 & break; }
done
sleep 3

installed() { dpkg-query -W -f='${Status}' rdpmanager 2>/dev/null | grep -q 'install ok installed'; }

VIA=
timeout 120 pkcon -p install-local -y /tmp/rdpmanager.deb > /tmp/pkcon.log 2>&1
RC=$?
if [[ $RC -eq 0 ]] && installed; then
  VIA=PackageKit
else
  echo "--- pkcon (codice $RC):"; tail -15 /tmp/pkcon.log
  echo "PackageKit non ha installato il pacchetto in questo container: provo gdebi (come Mint/aptdaemon)"
  if gdebi --non-interactive /tmp/rdpmanager.deb > /tmp/gdebi.log 2>&1 && installed; then
    VIA=gdebi
  else
    tail -20 /tmp/gdebi.log
    echo "FALLITO: né PackageKit né gdebi hanno installato il .deb"; exit 1
  fi
fi
echo "installazione via $VIA: ok"

[[ -x /usr/bin/rdpmanager ]] || { echo "FALLITO: manca /usr/bin/rdpmanager eseguibile"; exit 1; }
[[ -f /usr/share/applications/rdpmanager.desktop ]] || { echo "FALLITO: manca il file .desktop"; exit 1; }
[[ -f /usr/share/icons/hicolor/256x256/apps/rdpmanager.png ]] || { echo "FALLITO: manca l'icona"; exit 1; }
[[ -f /usr/share/doc/rdpmanager/copyright ]] || { echo "FALLITO: manca /usr/share/doc/rdpmanager/copyright"; exit 1; }
MISSING=$(for f in /opt/rdpmanager/RdpManager.Linux /opt/rdpmanager/*.so*; do ldd "$f" 2>&1 | grep 'not found' | grep -v lttng; done | sort -u)
if [[ -n "$MISSING" ]]; then echo "$MISSING"; echo "FALLITO: librerie native mancanti"; exit 1; fi
echo "file e librerie: ok"

# Rimozione come dal gestore grafico.
if [[ $VIA == PackageKit ]]; then
  timeout 300 pkcon -p remove -y rdpmanager > /tmp/pkcon-rm.log 2>&1 || { tail -10 /tmp/pkcon-rm.log; echo "FALLITO: rimozione via PackageKit"; exit 1; }
else
  apt-get remove -y -qq rdpmanager > /dev/null 2>&1
fi
[[ ! -e /opt/rdpmanager/RdpManager.Linux ]] || { echo "FALLITO: file rimasti dopo la rimozione"; exit 1; }
echo "rimozione: ok"
echo "OK: $PRETTY_NAME (via $VIA)"
