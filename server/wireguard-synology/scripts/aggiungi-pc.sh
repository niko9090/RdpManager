#!/bin/sh
# Aggiunge un PC alla VPN.
# Uso: aggiungi-pc.sh NOME CHIAVE_PUBBLICA [10.66.0.N]
# Da Synology:  docker exec wireguard sh /scripts/aggiungi-pc.sh Ufficio "chiave="
set -eu

DIR="${WG_DIR:-/config/wg_confs}"
CONF="$DIR/wg0.conf"
ENDPOINT="${WG_ENDPOINT:-syncnetpro.com:51820}"

[ $# -ge 2 ] || { echo "Uso: $0 NOME CHIAVE_PUBBLICA [10.66.0.N]" >&2; exit 1; }
NOME="$1"; CHIAVE="$2"; IP="${3:-}"
[ -f "$CONF" ] || { echo "Manca $CONF: lancia prima prima-generazione.sh" >&2; exit 1; }

case "$NOME" in *[!A-Za-z0-9._-]*|"") echo "Nome non valido: usa lettere, numeri, . _ -" >&2; exit 1;; esac
echo "$CHIAVE" | grep -Eq '^[A-Za-z0-9+/]{43}=$' || { echo "Chiave pubblica non valida (44 caratteri base64)." >&2; exit 1; }
if grep -q "$CHIAVE" "$CONF"; then echo "Questa chiave e' gia' presente." >&2; exit 1; fi
if grep -q "^# PC: $NOME\$" "$CONF"; then echo "Esiste gia' un PC di nome $NOME." >&2; exit 1; fi

if [ -z "$IP" ]; then
  N=2
  while grep -Eq "AllowedIPs = 10\.66\.0\.$N/32" "$CONF"; do N=$((N + 1)); done
  [ "$N" -le 254 ] || { echo "Rete piena." >&2; exit 1; }
  IP="10.66.0.$N"
else
  echo "$IP" | grep -Eq '^10\.66\.0\.([2-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-4])$' || { echo "Indirizzo non valido (da 10.66.0.2 a 10.66.0.254)." >&2; exit 1; }
  if grep -Eq "AllowedIPs = $(echo "$IP" | sed 's/\./\\./g')/32" "$CONF"; then echo "$IP e' gia' assegnato." >&2; exit 1; fi
fi

cat >> "$CONF" <<EOF

# PC: $NOME
[Peer]
PublicKey = $CHIAVE
AllowedIPs = $IP/32
EOF

# Ricarica senza interrompere i tunnel attivi
wg-quick strip "$CONF" > /tmp/wg0.strip
wg syncconf wg0 /tmp/wg0.strip
rm -f /tmp/wg0.strip

echo "PC '$NOME' aggiunto con indirizzo $IP/32."
echo "Da inserire nel client RdpManager:"
echo "  Chiave pubblica del server: $(cat "$DIR/server.pub")"
echo "  Endpoint: $ENDPOINT"
echo "  Indirizzo del PC: $IP/32"
echo "  Server (VPN): 10.66.0.1 - AllowedIPs: 10.66.0.0/24 - Keepalive: 25"
