#!/bin/sh
# Avvio di wireguard + rdpm-sync dopo un riavvio del Synology.
# Installato da deploy-synology.remote.sh in /usr/local/etc/rc.d/S99rdpm-avvio.sh (DSM lo lancia con "start").
#
# Perché serve: all'avvio di DSM Docker riparte i contenitori prima che esista /dev/net/tun, così
# "wireguard" esce con «error gathering device information ... /dev/net/tun» e "rdpm-sync", che usa la sua
# rete, esce con «cannot join network of a non running container». Docker non ritenta: resta acceso solo
# il proxy, che risponde 502, e login, sincronizzazione e VPN restano spenti finché qualcuno non li riavvia.
#
# Uso a mano: sh avvio-nas.sh adesso   (stessa cosa, in primo piano, con i messaggi a video)

DOCKER=/usr/local/bin/docker
LOG=/var/log/rdpm-avvio.log

log() { echo "$(date '+%F %T') $*"; }

running() { [ "$($DOCKER inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = "true" ]; }

ripristina() {
  # 1. il dispositivo tun (il modulo c'è in DSM, ma può essere caricato tardi)
  if [ ! -c /dev/net/tun ]; then
    insmod /lib/modules/tun.ko 2>/dev/null || true
    mkdir -p /dev/net
    [ -c /dev/net/tun ] || mknod /dev/net/tun c 10 200
    chmod 600 /dev/net/tun
    log "creato /dev/net/tun"
  fi
  # 2. Docker pronto (fino a 10 minuti dopo il boot)
  i=0
  until $DOCKER info >/dev/null 2>&1; do
    i=$((i + 1)); [ $i -gt 120 ] && { log "Docker non risponde: rinuncio"; return 1; }
    sleep 5
  done
  # 3. wireguard, poi rdpm-sync nella sua rete; ritenta finché /health risponde (fino a 5 minuti)
  i=0
  while :; do
    running wireguard || $DOCKER start wireguard >/dev/null 2>&1 || log "wireguard non parte (tentativo $i)"
    sleep 5
    if running wireguard; then
      running rdpm-sync || $DOCKER start rdpm-sync >/dev/null 2>&1 || log "rdpm-sync non parte (tentativo $i)"
      sleep 5
      if curl -fsS -m 5 http://127.0.0.1:8787/health >/dev/null 2>&1; then
        log "wireguard e rdpm-sync accesi, /health ok"
        return 0
      fi
    fi
    i=$((i + 1)); [ $i -gt 30 ] && { log "rdpm-sync non risponde dopo 30 tentativi"; return 1; }
    sleep 5
  done
}

case "$1" in
  start)
    # in background: non deve rallentare l'avvio di DSM
    ( ripristina >>"$LOG" 2>&1 ) </dev/null >/dev/null 2>&1 &
    ;;
  adesso)
    ripristina
    ;;
esac
exit 0
