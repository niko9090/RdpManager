#!/usr/bin/env bash
# Eseguito DENTRO un sistema Debian/Ubuntu pulito (container o chroot) da scripts/test-linux-install.ps1.
# Uso: test-linux-install.sh <file.deb>
# Installa il .deb con apt (solo le dipendenze dichiarate, senza Recommends), controlla le librerie native
# e avvia l'app con un display virtuale: se dopo qualche secondo è ancora viva l'avvio è riuscito.
set -u
DEB=$1
export DEBIAN_FRONTEND=noninteractive
. /etc/os-release
echo "== $PRETTY_NAME"

apt-get update -qq > /dev/null 2>&1 || { echo "FALLITO: apt-get update"; exit 1; }
# xvfb e xauth servono solo alla prova; la sola dipendenza dell'app è quanto dichiara il .deb.
apt-get install -y -qq --no-install-recommends xvfb xauth > /dev/null 2>&1 || { echo "FALLITO: installazione di xvfb"; exit 1; }
cp "$DEB" /tmp/rdpmanager.deb
if ! apt-get install -y --no-install-recommends /tmp/rdpmanager.deb > /tmp/apt-install.log 2>&1; then
  tail -25 /tmp/apt-install.log
  echo "FALLITO: apt install del .deb"; exit 1
fi
echo "apt install: ok"

[[ -x /usr/bin/rdpmanager ]] || { echo "FALLITO: manca /usr/bin/rdpmanager eseguibile"; exit 1; }
[[ -f /usr/share/applications/rdpmanager.desktop ]] || { echo "FALLITO: manca il file .desktop"; exit 1; }

# Librerie native mancanti (tranne liblttng-ust, facoltativa: serve solo alla traccia di .NET).
MISSING=$(for f in /opt/rdpmanager/RdpManager.Linux /opt/rdpmanager/*.so*; do ldd "$f" 2>&1 | grep 'not found' | grep -v lttng; done | sort -u)
if [[ -n "$MISSING" ]]; then echo "$MISSING"; echo "FALLITO: librerie native mancanti"; exit 1; fi

# Avvio: con Xvfb l'app deve restare in vita 8 secondi (timeout = codice 124).
export HOME=/tmp/prova-home; mkdir -p "$HOME"
timeout 8 xvfb-run -a /usr/bin/rdpmanager > /tmp/avvio.log 2>&1
RC=$?
if [[ $RC -ne 124 ]]; then
  head -20 /tmp/avvio.log
  echo "FALLITO: l'app è terminata da sola (codice $RC)"; exit 1
fi
echo "avvio: ok (l'app è rimasta in esecuzione)"

# Rimozione pulita.
apt-get remove -y -qq rdpmanager > /dev/null 2>&1 || { echo "FALLITO: rimozione"; exit 1; }
[[ ! -e /opt/rdpmanager/RdpManager.Linux ]] || { echo "FALLITO: file rimasti dopo la rimozione"; exit 1; }
echo "rimozione: ok"
echo "OK: $PRETTY_NAME"
