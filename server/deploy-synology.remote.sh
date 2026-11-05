#!/bin/bash
# Eseguito SUL Synology (come root) da deploy-synology.ps1. Idempotente. Non stampa segreti salvo la riga finale.
set -eu
export PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:/usr/syno/bin
BASE=/volume1/docker
DOCKER=/usr/local/bin/docker
DC="/usr/local/bin/docker-compose"
export COMPOSE_HTTP_TIMEOUT=600
HOST="${DEPLOY_HOST:-syncnetpro.com}"

say() { echo "[deploy] $*"; }

# 1. Docker
if [ ! -x "$DOCKER" ]; then
  say "Docker manca: lo installo"
  synopkg install_from_server Docker || { echo "installazione Docker fallita" >&2; exit 1; }
  for i in $(seq 1 30); do [ -x "$DOCKER" ] && break; sleep 5; done
fi
synopkg status Docker 2>/dev/null | grep -q 'started' || synopkg start Docker || true
for i in $(seq 1 30); do $DOCKER info >/dev/null 2>&1 && break; sleep 3; done
$DOCKER info >/dev/null

# 2. Sorgenti (gia' copiati in /tmp/rdpm-deploy dal PC)
SRC=/tmp/rdpm-deploy
for d in wireguard-synology:wireguard rdpm-sync:rdpm-sync caddy-synology:caddy; do
  from=${d%%:*}; to=$BASE/${d##*:}
  mkdir -p "$to"
  # tar sovrascrive solo i file del repo: config/, data/, run/ e .env restano
  (cd "$SRC/$from" && tar -cf - .) | (cd "$to" && tar -xf -)
done
find "$BASE/wireguard" "$BASE/rdpm-sync" "$BASE/caddy" -maxdepth 2 \( -name '*.sh' -o -name '*.yml' -o -name Caddyfile -o -name '*.modello' -o -name '.env.example' \) -print0 | xargs -0 sed -i 's/\r$//'
chmod +x "$BASE"/wireguard/scripts/*.sh
# riaccensione dopo un riavvio del NAS (Docker parte prima di /dev/net/tun: vedi lo script)
install -m 755 "$BASE/wireguard/scripts/avvio-nas.sh" /usr/local/etc/rc.d/S99rdpm-avvio.sh
mkdir -p "$BASE/wireguard/run" "$BASE/wireguard/config" "$BASE/rdpm-sync/data"
rm -rf "$SRC"

# 3. Costruzione immagini e prima generazione chiavi
(cd "$BASE/wireguard" && $DC build)
if [ ! -f "$BASE/wireguard/config/wg0.conf" ] || [ ! -f "$BASE/wireguard/config/server.pub" ]; then
  say "prima generazione delle chiavi del server WireGuard"
  $DOCKER run --rm -e WG_DIR=/config/wg_confs -v "$BASE/wireguard/config:/config/wg_confs" \
    -v "$BASE/wireguard/scripts:/scripts:ro" --entrypoint sh rdpmanager/wireguard-go:latest /scripts/prima-generazione.sh
fi

# 4. .env di rdpm-sync (mai sovrascritto: si aggiungono solo le chiavi mancanti)
ENVF=$BASE/rdpm-sync/.env
[ -f "$ENVF" ] || cp "$BASE/rdpm-sync/.env.example" "$ENVF"
chmod 600 "$ENVF"
rnd() { head -c 256 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "$1"; }
getv() { grep -E "^$1=" "$ENVF" | tail -1 | cut -d= -f2- || true; }
setv() { # setv CHIAVE VALORE: riempie la riga vuota o aggiunge la chiave
  if grep -qE "^$1=" "$ENVF"; then sed -i "s|^$1=.*|$1=$2|" "$ENVF"; else echo "$1=$2" >> "$ENVF"; fi
}
[ -n "$(getv RDPM_SYNC_ADMIN_KEY)" ] || { setv RDPM_SYNC_ADMIN_KEY "$(rnd 40)"; say "creata chiave admin"; }
if [ -z "$(getv RDPM_SYNC_REGISTRATION_CODE)" ] && [ -z "$(getv RDPM_SYNC_OPEN_REGISTRATION)" ]; then
  setv RDPM_SYNC_REGISTRATION_CODE "$(rnd 32)"; say "creato codice di registrazione"
fi
[ -n "$(getv RDPM_VPN_ENDPOINT)" ] || setv RDPM_VPN_ENDPOINT "$HOST:51820"

# 5. Certificato di DSM per $HOST
CERT_DIR=
for c in /usr/syno/etc/certificate/_archive/*/fullchain.pem; do
  [ -f "$c" ] || continue
  if openssl x509 -in "$c" -noout -ext subjectAltName 2>/dev/null | grep -qE "DNS:$HOST(,|$)"; then CERT_DIR=$(dirname "$c"); break; fi
done
[ -n "$CERT_DIR" ] || { echo "nessun certificato DSM per $HOST in /usr/syno/etc/certificate/_archive" >&2; exit 1; }
printf 'CERT_DIR=%s\nCERT_HOST=%s\n' "$CERT_DIR" "$HOST" > "$BASE/caddy/.env"
say "certificato: $CERT_DIR"

# 6. Avvio (wireguard prima, poi rdpm-sync nella sua rete, poi il proxy)
(cd "$BASE/wireguard" && $DC up -d)
(cd "$BASE/rdpm-sync" && $DC up -d --build --force-recreate)
(cd "$BASE/caddy" && $DC pull -q && $DC up -d --force-recreate)

# 6b. Porte sul router via UPnP (TCP 8443 e UDP 51820): se il router non lo permette, avvisa e prosegue
LANIP=$(ip -4 route get 1.1.1.1 | sed -n 's/.* src \([0-9.]*\).*/\1/p' | head -1)
$DOCKER run --rm --net host alpine sh -c "apk add -q miniupnpc >/dev/null 2>&1 && upnpc -a $LANIP 8443 8443 TCP && upnpc -a $LANIP 51820 51820 UDP" 2>&1 | grep -E 'redirected|failed|error|No IGD' \
  || say "ATTENZIONE: UPnP del router non disponibile: inoltra a mano TCP 8443 e UDP 51820 verso $LANIP"

# 7. Verifiche
ok=0
for i in $(seq 1 30); do curl -fsS http://127.0.0.1:8787/health && { ok=1; echo; break; }; sleep 2; done
[ $ok = 1 ] || { echo "/health non risponde" >&2; $DOCKER logs --tail 30 rdpm-sync >&2; exit 1; }
$DOCKER exec rdpm-sync wg show wg0 | head -5
sleep 3
curl -fsS --resolve "$HOST:8443:127.0.0.1" "https://$HOST:8443/health" && echo
$DOCKER ps --format '{{.Names}} {{.Status}}'
echo "REGISTRATION_CODE=$(getv RDPM_SYNC_REGISTRATION_CODE)"
