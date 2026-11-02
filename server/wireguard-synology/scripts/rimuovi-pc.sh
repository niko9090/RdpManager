#!/bin/sh
# Rimuove un PC dalla VPN.
# Uso: rimuovi-pc.sh NOME
set -eu
DIR="${WG_DIR:-/config/wg_confs}"
CONF="$DIR/wg0.conf"
[ $# -eq 1 ] || { echo "Uso: $0 NOME" >&2; exit 1; }
NOME="$1"
[ -f "$CONF" ] || { echo "Manca $CONF" >&2; exit 1; }
grep -q "^# PC: $NOME\$" "$CONF" || { echo "PC '$NOME' non trovato." >&2; exit 1; }

cp "$CONF" "$CONF.bak"
# Toglie il commento "# PC: nome" e il blocco [Peer] che lo segue (fino alla riga vuota o al blocco successivo)
awk -v nome="# PC: $NOME" '
  $0 == nome { salta = 1; next }
  salta && /^\[Peer\]/ { next }
  salta && /^(PublicKey|AllowedIPs|PresharedKey|PersistentKeepalive|Endpoint)/ { next }
  { salta = 0; print }
' "$CONF.bak" > "$CONF"

wg-quick strip "$CONF" > /tmp/wg0.strip
wg syncconf wg0 /tmp/wg0.strip
rm -f /tmp/wg0.strip
echo "PC '$NOME' rimosso (copia di sicurezza: wg0.conf.bak)."
