#!/bin/sh
# Prima generazione delle chiavi del server e di config/wg0.conf.
# Si lancia dal Synology:  docker exec wireguard sh /scripts/prima-generazione.sh
set -eu

DIR="${WG_DIR:-/config/wg_confs}"
CONF="$DIR/wg0.conf"
MODELLO="$DIR/wg0.conf.modello"

if [ -f "$CONF" ]; then
  echo "wg0.conf esiste gia': non lo tocco. Chiave pubblica del server:" >&2
  cat "$DIR/server.pub"
  exit 0
fi
[ -f "$MODELLO" ] || { echo "Manca $MODELLO" >&2; exit 1; }

umask 077
wg genkey > "$DIR/server.key"
wg pubkey < "$DIR/server.key" > "$DIR/server.pub"
CHIAVE=$(cat "$DIR/server.key")
sed "s|__CHIAVE_PRIVATA__|$CHIAVE|" "$MODELLO" > "$CONF"

echo "Fatto. Chiave pubblica del server:"
cat "$DIR/server.pub"
echo "Riavvia il container:  docker restart wireguard"
