#!/usr/bin/env bash
# Prova end-to-end del pacchetto per NAS in Docker (vedi e2e/docker-compose.yml): server WireGuard finto + rdpm-sync vero
# + due «NAS» Linux che installano il pacchetto con installa-nas.sh e il file .rdpmconfig esportato da rdpm-sync.
# Non tocca server o NAS veri. Serve Docker (Docker Desktop su Windows: da Git Bash o WSL).
#   bash server/nas-client/test/esegui-e2e.sh [--lascia-su]
set -uo pipefail
export MSYS_NO_PATHCONV=1
QUI=$(cd "$(dirname "$0")" && pwd)
cd "$QUI/e2e" || exit 1
DC=(docker compose -p mvpn-nas-e2e)
PASSATE=0; FALLITE=0
ok() { PASSATE=$((PASSATE + 1)); echo "  ok: $*"; }
ko() { FALLITE=$((FALLITE + 1)); echo "  FALLITA: $*"; }
conta() { # conta USCITA: somma i risultati stampati da nas-prova.sh
    local r p f
    r=$(grep -o 'risultato .*: [0-9]* passate, [0-9]* fallite' <<< "$1" | tail -n 1)
    p=$(sed -n 's/.*: \([0-9]*\) passate.*/\1/p' <<< "$r"); f=$(sed -n 's/.* \([0-9]*\) fallite/\1/p' <<< "$r")
    PASSATE=$((PASSATE + ${p:-0})); FALLITE=$((FALLITE + ${f:-1}))
}
nas() { local out; out=$("${DC[@]}" exec -T "$@" 2>&1); echo "$out"; conta "$out"; }
pulisci() {
    if [ "${1:-}" != "--lascia-su" ]; then "${DC[@]}" down -v --remove-orphans >/dev/null 2>&1; fi
    rm -f "$QUI/e2e/nas.rdpmconfig"
}
trap 'pulisci "${LASCIA:-}"' EXIT
[ "${1:-}" = "--lascia-su" ] && LASCIA=--lascia-su

echo "### avvio di server WireGuard finto, rdpm-sync e due NAS"
"${DC[@]}" down -v --remove-orphans >/dev/null 2>&1
# Prima il server WireGuard: rdpm-sync legge server.pub all'avvio (senza, risponde vpn_disabled).
"${DC[@]}" up -d --build --quiet-pull wgserver 2>&1 | tail -n 2 || { echo "docker compose up fallito"; exit 1; }
for _ in $(seq 1 60); do "${DC[@]}" exec -T wgserver test -f /vpn/pronto 2>/dev/null && break; sleep 1; done
"${DC[@]}" up -d --build --quiet-pull 2>&1 | tail -n 3 || { echo "docker compose up fallito"; exit 1; }
for _ in $(seq 1 120); do
    "${DC[@]}" exec -T nas-lan sh -c 'test -f /pronto && wget -qO- http://172.30.1.10:8787/health' 2>/dev/null | grep -q ok && break
    sleep 2
done
"${DC[@]}" exec -T nas-fuori sh -c 'until test -f /pronto; do sleep 1; done'
if "${DC[@]}" exec -T nas-lan wget -qO- http://172.30.1.10:8787/health 2>/dev/null | grep -q ok; then ok "rdpm-sync risponde"; else ko "rdpm-sync non risponde"; "${DC[@]}" logs rdpm-sync | tail -n 30; exit 1; fi

echo "### file .rdpmconfig esportato da rdpm-sync (come per i PC)"
"${DC[@]}" exec -T rdpm-sync dotnet rdpm-sync.dll export-client-config --url http://vpn.prova.test:8787 --url-interno http://172.30.1.10:8787 \
    > "$QUI/e2e/nas.rdpmconfig"
if grep -q '"codiceRegistrazione": "codice-di-prova-lungo-e-casuale"' "$QUI/e2e/nas.rdpmconfig"; then ok "file di configurazione esportato"; else ko "file di configurazione"; cat "$QUI/e2e/nas.rdpmconfig"; fi

echo "### NAS nella LAN del server"
nas nas-lan sh /prova/nas-prova.sh installa lan
echo "### NAS fuori casa"
nas nas-fuori sh /prova/nas-prova.sh installa pubblico

echo "### lato server"
peers=$("${DC[@]}" exec -T wgserver /pkg/bin/amd64/wg show wg0 latest-handshakes)
n=$(awk '$2 > 0' <<< "$peers" | wc -l)
if [ "$n" -eq 2 ]; then ok "il server vede 2 NAS con handshake"; else ko "handshake sul server: $peers"; fi
conf=$("${DC[@]}" exec -T wgserver cat /vpn/wg0.conf)
if grep -q '# PC: .*DS-LAN' <<< "$conf" && grep -q '# PC: .*DS-Fuori' <<< "$conf"; then ok "rdpm-sync ha scritto i due NAS in wg0.conf"; else ko "wg0.conf: $conf"; fi

echo "### da NAS a NAS attraverso la VPN"
ip_lan=$("${DC[@]}" exec -T nas-lan sed -n 's/^MVPN_INDIRIZZO=\(.*\)\/32$/\1/p' /volume1/@marshmellovpn/stato/profilo.env | tr -d '\r')
nas nas-fuori sh /prova/nas-prova.sh ping "$ip_lan"

echo "### ricreazione del tunnel (UDP bloccato) sul NAS in LAN"
nas nas-lan sh /prova/nas-prova.sh ricreazione
echo "### riavvio simulato (script di avvio di DSM)"
nas nas-lan sh /prova/nas-prova.sh riavvio

echo "### disinstallazione del NAS fuori casa"
pub=$("${DC[@]}" exec -T nas-fuori cat /volume1/@marshmellovpn/stato/pubblica.key | tr -d '\r')
nas nas-fuori sh /prova/nas-prova.sh disinstalla
if "${DC[@]}" exec -T wgserver /pkg/bin/amd64/wg show wg0 peers | grep -qF "$pub"; then ko "il NAS disinstallato è ancora sul server"; else ok "il NAS disinstallato non è più sul server"; fi

echo ""
echo "esegui-e2e: $PASSATE passate, $FALLITE fallite"
[ "$FALLITE" = 0 ]
