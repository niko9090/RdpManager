#!/bin/sh
# Prove delle funzioni di marshmellovpn.sh senza rete né root: scelta dell'endpoint, file per «wg setconf» e il ciclo
# del supervisore con orologio finto (stessi casi di tests/RdpManager.Tests/VpnServiceLoopTests.cs).
#   sh server/nas-client/test/test-unita.sh      (va con sh/ash/dash/bash)
# shellcheck disable=SC2034,SC2329 # variabili e funzioni finte le usa marshmellovpn.sh
set -u
QUI=$(cd "$(dirname "$0")" && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
export MVPN_DIR="$TMP/inst"
mkdir -p "$MVPN_DIR/stato" "$MVPN_DIR/run" "$MVPN_DIR/log"
MVPN_SOLO_FUNZIONI=1
# shellcheck source=../marshmellovpn.sh disable=SC1091
. "$QUI/../marshmellovpn.sh"

PASSATE=0; FALLITE=0
ok() { PASSATE=$((PASSATE + 1)); }
ko() { FALLITE=$((FALLITE + 1)); echo "FALLITA: $*"; }
uguale() { if [ "$1" = "$2" ]; then ok; else ko "$3: «$1» invece di «$2»"; fi; }
vero() { if "$@"; then ok; else ko "atteso vero: $*"; fi; }
falso() { if "$@"; then ko "atteso falso: $*"; else ok; fi; }

# ---------------------------------------------------------------- su_lan_del_server
vero su_lan_del_server 192.168.1.10:51820 "192.168.1.50"
vero su_lan_del_server 192.168.1.10:51820 "10.0.0.2 192.168.1.7"
falso su_lan_del_server 192.168.1.10:51820 "192.168.2.50"
falso su_lan_del_server 192.168.1.10:51820 ""
falso su_lan_del_server vpn.example.com:51820 "192.168.1.50"
falso su_lan_del_server 127.0.0.1:51820 "127.0.0.5"
falso su_lan_del_server 1.2.3.4.5:51820 "1.2.3.4"
falso su_lan_del_server 192.168.1.10:51820 "192.168.10.5" # 192.168.1 non è prefisso di 192.168.10

# ---------------------------------------------------------------- ipv4_locali (ip finto)
ip() {
    cat <<'EOF'
1: lo    inet 127.0.0.1/8 scope host lo\       valid_lft forever preferred_lft forever
2: eth0    inet 192.168.1.50/24 brd 192.168.1.255 scope global eth0\       valid_lft forever preferred_lft forever
5: tailscale0    inet 100.101.102.103/32 scope global tailscale0\       valid_lft forever preferred_lft forever
9: mvpn0    inet 10.66.0.9/32 scope global mvpn0\       valid_lft forever preferred_lft forever
EOF
}
uguale "$(ipv4_locali | tr '\n' ' ')" "192.168.1.50 100.101.102.103 " "ipv4_locali"
unset -f ip

# ---------------------------------------------------------------- profilo e conf
# profilo [ENDPOINT_LAN]: scrive profilo.env come farebbe mvpn-helper (il supervisore lo rilegge a ogni accensione).
profilo() {
    cat > "$MVPN_STATO/profilo.env" <<EOF
MVPN_NOME=ds
MVPN_INDIRIZZO=10.66.0.9/32
MVPN_CHIAVE_SERVER=hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=
MVPN_ENDPOINT=vpn.example.com:51820
MVPN_ENDPOINT_LAN=${1:-}
MVPN_ALLOWED_IPS=10.66.0.0/24
MVPN_RETE=10.66.0.0/24
MVPN_KEEPALIVE=25
EOF
}
profilo 192.168.1.10:51820
echo "d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3c=" > "$MVPN_STATO/privata.key"
vero carica_profilo
S_USA_LAN=1; uguale "$(scegli_endpoint)" "192.168.1.10:51820" "endpoint LAN"
S_USA_LAN=0; uguale "$(scegli_endpoint)" "vpn.example.com:51820" "endpoint pubblico"
scrivi_conf "$TMP/c.conf" vpn.example.com:51820
atteso="[Interface]
PrivateKey = d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3d3c=

[Peer]
PublicKey = hSDwCYkwp1R0i33ctD73Wg2/Og0mOBr066SpjqqbTmo=
AllowedIPs = 10.66.0.0/24
Endpoint = vpn.example.com:51820
PersistentKeepalive = 25"
uguale "$(grep -v '^#' "$TMP/c.conf")" "$atteso" "file per wg setconf"
uguale "$(find "$TMP/c.conf" -perm 600 | wc -l | tr -d ' ')" 1 "permessi 600 del file con la chiave privata"
if grep -q ListenPort "$TMP/c.conf"; then ko "niente ListenPort (porta nuova a ogni ricreazione)"; else ok; fi

# ---------------------------------------------------------------- ciclo con orologio finto
# Finti: orologio, tunnel, handshake, rete locale. ACCENSIONI = endpoint usati a ogni accensione.
ORA=1000000
adesso() { echo "$ORA"; }
registra_riga() { RIGHE="${RIGHE:-}$*|"; }
ACCESO=0; HS_FINTO=0; LOCALI="192.168.1.50"; ACCENSIONI=""; FALLISCI=0
tunnel_attivo() { [ "$ACCESO" = 1 ]; }
ferma_tunnel() { ACCESO=0; }
accendi() {
    if [ "$FALLISCI" = 1 ]; then ERRORE="nome non risolto"; return 1; fi
    ACCESO=1; HS_FINTO=0; ACCENSIONI="$ACCENSIONI $1"; return 0
}
leggi_handshake() { HS=$HS_FINTO; LETTO=1; }
ipv4_locali() { echo "$LOCALI"; }
wg_cmd() { echo 40000; }
nuovo() {
    S_USA_LAN=""; S_RISPOSTO=0; S_TENTATIVO=0; S_PROSSIMO=0; S_RICREAZIONI=0
    ACCESO=0; HS_FINTO=0; ACCENSIONI=""; RIGHE=""; FALLISCI=0; ORA=1000000
    rm -f "$MVPN_RUN/collegato"
}
# corri SECONDI [RISPONDE]: avanza l'orologio di 5 s alla volta; con RISPONDE=1 il server risponde (handshake ogni 120 s).
corri() {
    fine=$((ORA + $1)); risponde=${2:-0}
    while [ "$ORA" -lt "$fine" ]; do
        if [ "$risponde" = 1 ] && [ "$ACCESO" = 1 ] && { [ "$HS_FINTO" = 0 ] || [ $((ORA - HS_FINTO)) -ge 120 ]; }; then HS_FINTO=$ORA; fi
        passo
        ORA=$((ORA + 5))
    done
}

# 1. Tunnel vivo (handshake ogni 2 minuti): non si tocca per un'ora.
nuovo; profilo ""
corri 3600 1
uguale "$S_RICREAZIONI" 0 "tunnel vivo: nessuna ricreazione"
uguale "$ACCENSIONI" " vpn.example.com:51820" "tunnel vivo: una sola accensione"
vero test -f "$MVPN_RUN/collegato"

# 2. Rispondeva e smette: ricreato dopo 200 s dall'ultimo handshake, con il nome risolto di nuovo (nuova accensione).
nuovo; profilo ""
corri 60 1
corri 130 0
uguale "$S_RICREAZIONI" 0 "prima dei 200 s non si ricrea"
corri 60 0
uguale "$S_RICREAZIONI" 1 "dopo 200 s si ricrea"
uguale "$ACCENSIONI" " vpn.example.com:51820 vpn.example.com:51820" "ricreato con il nome pubblico"
case "$RIGHE" in *"Ultimo handshake 2"*"s fa: tunnel ricreato."*) ok ;; *) ko "motivo nel registro: $RIGHE" ;; esac

# 3. Senza endpoint LAN e senza nessuna risposta: si ricrea dopo 2 minuti.
nuovo; profilo ""
corri 115 0
uguale "$S_RICREAZIONI" 0 "senza risposta, prima dei 120 s"
corri 15 0
uguale "$S_RICREAZIONI" 1 "senza risposta, dopo 120 s"

# 4. Con endpoint LAN e NAS nella LAN: parte dalla LAN; se cade sceglie di nuovo (sempre LAN).
nuovo; profilo 192.168.1.10:51820; LOCALI="192.168.1.50"
corri 60 1
uguale "$ACCENSIONI" " 192.168.1.10:51820" "nella LAN del server: endpoint LAN"
corri 160 0
uguale "$S_RICREAZIONI" 1 "LAN: ricreato quando smette di rispondere"
uguale "${ACCENSIONI##* }" "192.168.1.10:51820" "LAN: di nuovo l'endpoint LAN"

# 5. Fuori dalla LAN: pubblico; se in 20 s nessuno risponde prova la LAN, poi di nuovo il pubblico.
nuovo; profilo 192.168.1.10:51820; LOCALI="10.1.2.3"
corri 55 0
uguale "$ACCENSIONI" " vpn.example.com:51820 192.168.1.10:51820 vpn.example.com:51820" "alternanza pubblico/LAN senza risposta"

# 6. Accensione fallita (nome non risolto): il supervisore non si ferma e riprova dopo 15 s.
nuovo; profilo ""; FALLISCI=1
corri 10 0
uguale "$ACCENSIONI" "" "accensione fallita: niente tunnel"
case "$RIGHE" in *"riprovo fra 15 s: nome non risolto"*) ok ;; *) ko "riga di errore: $RIGHE" ;; esac
FALLISCI=0
corri 10 0
uguale "$ACCENSIONI" " vpn.example.com:51820" "dopo 15 s riprova e accende"

# 7. wireguard-go muore: si riaccende al passo dopo.
nuovo; profilo ""
corri 30 1
ACCESO=0
corri 10 1
uguale "$ACCENSIONI" " vpn.example.com:51820 vpn.example.com:51820" "riaccensione dopo la morte di wireguard-go"

# 8. wg non risponde (socket sparito): ricreato subito.
nuovo; profilo ""
corri 10 1
leggi_handshake() { HS=0; LETTO=0; }
corri 5 1
uguale "$S_RICREAZIONI" 1 "wg show fallito: ricreato"
leggi_handshake() { HS=$HS_FINTO; LETTO=1; }

echo "test-unita: $PASSATE passate, $FALLITE fallite"
[ "$FALLITE" = 0 ]
