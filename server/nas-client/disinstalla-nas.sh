#!/bin/sh
# Toglie marshmelloVPN dal NAS: ferma tunnel e supervisore, toglie il NAS dalla VPN sul server, cancella lo script
# di avvio e la cartella. Tailscale, firewall e il resto del NAS non vengono toccati.
#
#   sudo sh disinstalla-nas.sh [--cartella DIR] [--tieni-sul-server] [--tieni-chiavi]
#
#   --tieni-sul-server  non toglie il NAS dal server (per reinstallarlo con lo stesso indirizzo)
#   --tieni-chiavi      lascia la cartella «stato» (chiavi e account): una nuova installazione riprende lo stesso indirizzo
set -eu
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH
RCD=${MVPN_RCD:-/usr/local/etc/rc.d}
RCD_FILE=S99marshmellovpn.sh

errore() { echo "ERRORE: $*" >&2; exit 1; }

cartella=""; server=1; chiavi=0
while [ $# -gt 0 ]; do
    case "$1" in
        --cartella) cartella=${2:-}; shift 2 || errore "manca la cartella dopo --cartella" ;;
        --tieni-sul-server) server=0; shift ;;
        --tieni-chiavi) chiavi=1; shift ;;
        -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) errore "parametro sconosciuto: $1" ;;
    esac
done
[ "$(id -u)" = 0 ] || errore "serve l'amministratore: lancia «sudo sh disinstalla-nas.sh»."

if [ -z "$cartella" ] && [ -f "$RCD/$RCD_FILE" ]; then
    cartella=$(sed -n 's/^MVPN_DIR=//p' "$RCD/$RCD_FILE" | head -n 1)
fi
if [ -z "$cartella" ]; then
    here=$(cd "$(dirname "$0")" && pwd)
    if [ -f "$here/marshmellovpn.sh" ] && [ -d "$here/stato" ]; then cartella=$here; fi
fi
if [ -z "$cartella" ]; then
    for d in /volume*/@marshmellovpn; do
        [ -f "$d/marshmellovpn.sh" ] && { cartella=$d; break; }
    done
fi
[ -n "$cartella" ] && [ -f "$cartella/marshmellovpn.sh" ] || errore "non trovo l'installazione: indica la cartella con --cartella."
case "$cartella" in
    /|/volume[0-9]|/volume[0-9]/|/usr|/usr/local) errore "cartella «$cartella» non valida." ;;
esac
echo "Installazione: $cartella"

MVPN_DIR=$cartella /bin/sh "$cartella/marshmellovpn.sh" ferma || true

if [ "$server" = 1 ] && [ -f "$cartella/stato/account.json" ]; then
    if ! "$cartella/bin/mvpn-helper" rimuovi --stato "$cartella/stato"; then
        echo "Attenzione: il NAS resta registrato sul server (si può togliere dalla pagina /vpn del server o dall'app)."
    fi
fi

rm -f "$RCD/$RCD_FILE"
if [ "$chiavi" = 1 ]; then
    find "$cartella" -mindepth 1 -maxdepth 1 ! -name stato -exec rm -rf {} +
    echo "Disinstallato; chiavi e account restano in $cartella/stato."
else
    rm -rf "$cartella"
    echo "Disinstallato: cartella $cartella cancellata."
fi
rm -f /var/log/marshmellovpn-avvio.log
