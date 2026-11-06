#!/bin/sh
# Crea un PC con le sue chiavi sul server e scrive il profilo client completo (.conf).
# Uso: crea-profilo-client.sh NOME [10.66.0.N]
# Il file finisce in config/profili/NOME.conf (contiene la chiave privata del PC: non
# metterlo mai nel repository) e si puo' importare in RdpManager o nel client WireGuard.
set -eu

DIR="${WG_DIR:-/config/wg_confs}"
ENDPOINT="${WG_ENDPOINT:?imposta WG_ENDPOINT (nome-pubblico:porta, per esempio vpn.example.com:51820)}"
[ $# -ge 1 ] || { echo "Uso: $0 NOME [10.66.0.N]" >&2; exit 1; }
NOME="$1"; IP="${2:-}"
[ -f "$DIR/server.pub" ] || { echo "Manca $DIR/server.pub: lancia prima prima-generazione.sh" >&2; exit 1; }

umask 077
PRIV=$(wg genkey)
PUB=$(echo "$PRIV" | wg pubkey)
sh "$(dirname "$0")/aggiungi-pc.sh" "$NOME" "$PUB" $IP > /tmp/aggiungi.out || { cat /tmp/aggiungi.out >&2; exit 1; }
rm -f /tmp/aggiungi.out
IP=$(awk -v n="# PC: $NOME" '$0==n {f=1} f && /^AllowedIPs/ {print $3; exit}' "$DIR/wg0.conf")

mkdir -p "$DIR/profili"
cat > "$DIR/profili/$NOME.conf" <<EOT
[Interface]
PrivateKey = $PRIV
Address = $IP

[Peer]
PublicKey = $(cat "$DIR/server.pub")
Endpoint = $ENDPOINT
AllowedIPs = 10.66.0.0/24
PersistentKeepalive = 25
EOT
echo "Profilo scritto in $DIR/profili/$NOME.conf (indirizzo $IP)"
