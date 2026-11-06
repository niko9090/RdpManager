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
# WG_LAN = rete locale da raggiungere dalla VPN (CIDR); vuota = niente inoltro verso la LAN (righe PostUp/PostDown tolte)
if [ -n "${WG_LAN:-}" ]; then
  sed -e "s|__CHIAVE_PRIVATA__|$CHIAVE|" -e "s|__RETE_LAN__|$WG_LAN|g" "$MODELLO" > "$CONF"
else
  sed -e "s|__CHIAVE_PRIVATA__|$CHIAVE|" -e '/^PostUp\|^PostDown/d' "$MODELLO" > "$CONF"
fi

echo "Fatto. Chiave pubblica del server:"
cat "$DIR/server.pub"
echo "Riavvia il container:  docker restart wireguard"
