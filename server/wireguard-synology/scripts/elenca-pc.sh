#!/bin/sh
# Elenca i PC configurati (nome, indirizzo, chiave) e l'ultimo handshake.
set -eu
DIR="${WG_DIR:-/config/wg_confs}"
CONF="$DIR/wg0.conf"
[ -f "$CONF" ] || { echo "Manca $CONF" >&2; exit 1; }

awk '
  /^# PC: /   { nome = substr($0, 7) }
  /^PublicKey/ { chiave = $3 }
  /^AllowedIPs/ { printf "%-20s %-16s %s\n", nome, $3, chiave }
' "$CONF"

echo
echo "Stato attuale (wg show):"
wg show wg0 latest-handshakes 2>/dev/null || echo "(interfaccia wg0 non attiva)"
