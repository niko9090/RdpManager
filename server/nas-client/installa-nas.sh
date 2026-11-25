#!/bin/sh
# Installa marshmelloVPN su un NAS Synology (DSM 7, aarch64 o x86_64): il NAS entra nella VPN come un PC qualunque.
# Si lancia una volta via SSH, dalla cartella del pacchetto:
#
#   sudo sh installa-nas.sh --config /percorso/RdpManager.rdpmconfig [--nome NOME] [--cartella DIR]
#
# Parametri facoltativi (vincono sul file): --server URL --server-interno URL --codice CODICE --utente U --password P
#   --endpoint HOST:PORTA --chiave-server CHIAVE --rete CIDR. Senza --config servono almeno --server e --codice.
#   --cartella DIR          dove installare (predefinita /volume1/@marshmellovpn)
#   --senza-avvio-automatico non installa lo script di avvio in /usr/local/etc/rc.d
#   --non-avviare           installa e registra ma non accende il tunnel
# Rilanciarlo aggiorna i file e tiene chiavi e indirizzo (cartella «stato»).
set -eu
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH

PKG=$(cd "$(dirname "$0")" && pwd)
RCD=${MVPN_RCD:-/usr/local/etc/rc.d}
RCD_FILE=S99marshmellovpn.sh
ATTESA=${MVPN_ATTESA:-60}

errore() { echo "ERRORE: $*" >&2; exit 1; }
info() { echo "$*"; }

config=""; cartella=""; nome=""; avvio_auto=1; avvia=1
extra=""   # parametri per mvpn-helper, già controllati (niente spazi: vedi aggiungi)
aggiungi() {
    case "$2" in
        *[[:space:]\"\'\`\$\\]*) errore "il valore di $1 contiene spazi o caratteri non ammessi" ;;
        "") errore "manca il valore di $1" ;;
    esac
    extra="$extra $1 $2"
}
while [ $# -gt 0 ]; do
    case "$1" in
        --config) config=${2:-}; shift 2 || errore "manca il file dopo --config" ;;
        --cartella) cartella=${2:-}; shift 2 || errore "manca la cartella dopo --cartella" ;;
        --nome) nome=${2:-}; shift 2 || errore "manca il nome dopo --nome" ;;
        --server|--server-interno|--codice|--utente|--password|--endpoint|--chiave-server|--rete)
            [ $# -ge 2 ] || errore "manca il valore di $1"
            aggiungi "$1" "$2"; shift 2 ;;
        --senza-avvio-automatico) avvio_auto=0; shift ;;
        --non-avviare) avvia=0; shift ;;
        -h|--help|aiuto) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *.rdpmconfig) config=$1; shift ;;
        *) errore "parametro sconosciuto: $1 (vedi sh installa-nas.sh --help)" ;;
    esac
done

[ "$(id -u)" = 0 ] || errore "serve l'amministratore: lancia «sudo sh installa-nas.sh ...» (DSM chiede la password del tuo utente)."
if [ -n "$config" ]; then
    [ -f "$config" ] || errore "il file di configurazione «$config» non esiste."
    config=$(cd "$(dirname "$config")" && pwd)/$(basename "$config")
elif [ -z "$extra" ]; then
    errore "indica il file di configurazione: sudo sh installa-nas.sh --config /volume1/homes/TUOUTENTE/RdpManager.rdpmconfig"
fi

# ---------------------------------------------------------------- controlli
case "$(uname -m)" in
    x86_64|amd64) arch=amd64 ;;
    aarch64|arm64) arch=arm64 ;;
    *) errore "processore «$(uname -m)» non supportato: il pacchetto ha i programmi per x86_64 e aarch64 (arm64)." ;;
esac
dsm="sconosciuta"
if [ -f /etc.defaults/VERSION ]; then
    dsm=$(sed -n 's/^productversion="\(.*\)"/\1/p' /etc.defaults/VERSION)
fi
info "NAS: DSM $dsm, kernel $(uname -r), $(uname -m) -> programmi $arch."
command -v ip >/dev/null 2>&1 || errore "manca il comando «ip» (iproute2): su DSM c'è sempre, controlla di essere sul NAS."
for f in wireguard-go wg mvpn-helper; do
    [ -f "$PKG/bin/$arch/$f" ] || errore "manca $PKG/bin/$arch/$f: il pacchetto è incompleto (copia di nuovo tutta la cartella)."
done
if command -v sha256sum >/dev/null 2>&1 && [ -f "$PKG/bin/SHA256SUMS" ]; then
    (cd "$PKG/bin" && grep " $arch/" SHA256SUMS | sha256sum -c - >/dev/null 2>&1) \
        || errore "i programmi in bin/$arch non corrispondono a bin/SHA256SUMS: copia di nuovo il pacchetto (file rovinati o modificati)."
    info "Programmi controllati (SHA256SUMS)."
fi
if [ ! -c /dev/net/tun ]; then
    [ -f /lib/modules/tun.ko ] && insmod /lib/modules/tun.ko 2>/dev/null || true
    [ -c /dev/net/tun ] || { mkdir -p /dev/net; mknod /dev/net/tun c 10 200 2>/dev/null || true; }
fi
[ -c /dev/net/tun ] || errore "/dev/net/tun non c'è: questo NAS non permette interfacce TUN, wireguard-go non può partire."
if ip link show tailscale0 >/dev/null 2>&1 || [ -d /var/packages/Tailscale ]; then
    info "Tailscale c'è: non lo tocco (le due VPN convivono, usano reti diverse)."
fi

# ---------------------------------------------------------------- cartella
if [ -z "$cartella" ]; then
    for d in /volume*/@marshmellovpn; do
        [ -f "$d/marshmellovpn.sh" ] && { cartella=$d; break; }
    done
fi
if [ -z "$cartella" ]; then
    for v in /volume1 /volume2 /volume3 /volume4; do
        [ -d "$v" ] && { cartella=$v/@marshmellovpn; break; }
    done
fi
[ -n "$cartella" ] || errore "non trovo nessun volume (/volume1...): indica dove installare con --cartella."
case "$cartella" in
    /*) ;;
    *) errore "--cartella deve essere un percorso assoluto (per esempio /volume1/@marshmellovpn)." ;;
esac
case "$cartella" in
    *[[:space:]]*) errore "la cartella non deve contenere spazi." ;;
esac
info "Cartella: $cartella"

if [ -f "$cartella/marshmellovpn.sh" ]; then
    info "Installazione già presente: la aggiorno (chiavi e indirizzo restano)."
    MVPN_DIR=$cartella /bin/sh "$cartella/marshmellovpn.sh" ferma >/dev/null 2>&1 || true
fi
umask 022
mkdir -p "$cartella/bin" "$cartella/log" "$cartella/run"
mkdir -p "$cartella/stato"
chmod 700 "$cartella/stato"
for f in wireguard-go wg mvpn-helper; do
    cp "$PKG/bin/$arch/$f" "$cartella/bin/$f.nuovo"
    chmod 755 "$cartella/bin/$f.nuovo"
    mv -f "$cartella/bin/$f.nuovo" "$cartella/bin/$f"
done
cp "$PKG/marshmellovpn.sh" "$PKG/disinstalla-nas.sh" "$cartella/"
chmod 755 "$cartella/marshmellovpn.sh" "$cartella/disinstalla-nas.sh"
[ -f "$PKG/bin/VERSIONI.txt" ] && cp "$PKG/bin/VERSIONI.txt" "$cartella/bin/"
chown -R root:root "$cartella" 2>/dev/null || true
info "File copiati ($("$cartella/bin/mvpn-helper" versione 2>/dev/null | sed 's/^/mvpn-helper /'), $("$cartella/bin/wg" --version 2>/dev/null | cut -d' ' -f1-2))."

# ---------------------------------------------------------------- registrazione
set -- registra --stato "$cartella/stato"
[ -n "$config" ] && set -- "$@" --config "$config"
[ -n "$nome" ] && set -- "$@" --nome "$nome"
# shellcheck disable=SC2086 # $extra è una lista di parametri senza spazi (controllata da aggiungi)
set -- "$@" $extra
info "Registrazione del NAS sul server (qualche secondo: la chiave privata nasce qui e non esce dal NAS)..."
if ! "$cartella/bin/mvpn-helper" "$@"; then
    errore "registrazione non riuscita (vedi sopra). I file sono installati in $cartella: corretto il problema, rilancia lo stesso comando."
fi

# ---------------------------------------------------------------- avvio automatico
if [ "$avvio_auto" = 1 ]; then
    mkdir -p "$RCD"
    sed "s|^MVPN_DIR=.*|MVPN_DIR=$cartella|" "$PKG/avvio-dsm.sh" > "$RCD/$RCD_FILE.nuovo"
    chmod 755 "$RCD/$RCD_FILE.nuovo"
    mv -f "$RCD/$RCD_FILE.nuovo" "$RCD/$RCD_FILE"
    info "Avvio automatico: $RCD/$RCD_FILE (DSM lo lancia a ogni accensione)."
fi

if [ "$avvia" = 0 ]; then
    info "Installato. Per accendere: sudo sh $cartella/marshmellovpn.sh avvia"
    exit 0
fi

# ---------------------------------------------------------------- accensione
MVPN_DIR=$cartella /bin/sh "$cartella/marshmellovpn.sh" avvia
info "Attendo il primo contatto col server (fino a $ATTESA s)..."
i=0; ok=0
while [ $i -lt "$ATTESA" ]; do
    if [ -f "$cartella/run/collegato" ]; then ok=1; break; fi
    sleep 2; i=$((i + 2))
done
# shellcheck disable=SC1091
. "$cartella/stato/profilo.env"
echo ""
if [ $ok = 1 ]; then
    info "FATTO: il NAS è nella VPN con l'indirizzo ${MVPN_INDIRIZZO%/*}."
    info "Dagli altri PC della VPN: \\\\${MVPN_INDIRIZZO%/*} (cartelle condivise), https://${MVPN_INDIRIZZO%/*}:5001 (DSM), ssh ${MVPN_INDIRIZZO%/*}."
else
    info "Il tunnel è acceso ma il server non ha ancora risposto: il supervisore continua a provare da solo."
    info "Controlla fra qualche minuto con: sudo sh $cartella/marshmellovpn.sh stato"
fi
info "Se il firewall di DSM è attivo, consenti la rete 10.66.0.0/24 (vedi LEGGIMI.md, «Firewall»)."
info "Registro: $cartella/log/marshmellovpn.log   Disinstallare: sudo sh $cartella/disinstalla-nas.sh"
info "Il file .rdpmconfig contiene il codice di registrazione: ora che il NAS è registrato puoi cancellarlo."
