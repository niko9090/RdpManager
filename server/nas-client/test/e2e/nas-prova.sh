#!/bin/sh
# Gira DENTRO un contenitore «NAS» della prova end-to-end (vedi docker-compose.yml).
#   sh /prova/nas-prova.sh installa lan|pubblico   installa dal pacchetto, controlla file, endpoint, ping al server
#   sh /prova/nas-prova.sh ricreazione             blocca l'UDP verso il server: il tunnel va ricreato con porta nuova
#   sh /prova/nas-prova.sh riavvio                 ferma tutto e riparte dallo script di avvio di DSM (come al boot)
#   sh /prova/nas-prova.sh ping IP                 ping attraverso la VPN
#   sh /prova/nas-prova.sh disinstalla
set -u
DIR=/volume1/@marshmellovpn
LOG=$DIR/log/marshmellovpn.log
# Tempi corti per la prova (sul NAS valgono 200/120/20/15 s).
export MVPN_PASSO=2 MVPN_TIMEOUT_HANDSHAKE=40 MVPN_TIMEOUT_PRIMO=30 MVPN_TIMEOUT_CAMBIO=8 MVPN_RITARDO=4 MVPN_ATTESA=40

PASSATE=0; FALLITE=0
ok() { PASSATE=$((PASSATE + 1)); echo "  ok: $*"; }
ko() { FALLITE=$((FALLITE + 1)); echo "  FALLITA: $*"; }
check() { descr=$1; shift; if "$@" >/dev/null 2>&1; then ok "$descr"; else ko "$descr"; fi; }
fine() { echo "risultato $(hostname): $PASSATE passate, $FALLITE fallite"; [ "$FALLITE" = 0 ]; }

aspetta() { # aspetta SECONDI COMANDO...
    t=$1; shift; i=0
    while [ $i -lt "$t" ]; do "$@" >/dev/null 2>&1 && return 0; sleep 1; i=$((i + 1)); done
    return 1
}
porta() { "$DIR/bin/wg" show mvpn0 listen-port 2>/dev/null; }
handshake() { "$DIR/bin/wg" show mvpn0 latest-handshakes 2>/dev/null | awk '{print $2}'; }
ha_handshake() { [ "$(handshake)" -gt 0 ] 2>/dev/null; }

case "${1:-}" in
installa)
    atteso=$2
    cp /prova/nas.rdpmconfig /tmp/nas.rdpmconfig
    sh /pkg/installa-nas.sh --config /tmp/nas.rdpmconfig --cartella "$DIR"
    rc=$?
    check "installa-nas.sh termina senza errori (rc=$rc)" test "$rc" = 0
    check "chiave privata con permessi 600" test "$(stat -c %a "$DIR/stato/privata.key")" = 600
    check "cartella stato con permessi 700" test "$(stat -c %a "$DIR/stato")" = 700
    acc="pc-$(hostname | tr '[:upper:]' '[:lower:]')"
    check "account del dispositivo $acc" grep -q "\"utente\": \"$acc\"" "$DIR/stato/account.json"
    check "profilo con indirizzo 10.66.0.N/32" grep -Eq '^MVPN_INDIRIZZO=10\.66\.0\.[0-9]+/32$' "$DIR/stato/profilo.env"
    check "profilo con endpoint LAN dal server" grep -q '^MVPN_ENDPOINT_LAN=172.30.1.10:51820$' "$DIR/stato/profilo.env"
    check "il profilo non contiene la chiave privata" sh -c "! grep -q \"\$(cat $DIR/stato/privata.key)\" $DIR/stato/profilo.env"
    check "conf generato con permessi 600" test "$(stat -c %a "$DIR/stato/mvpn0.conf")" = 600
    if [ "$atteso" = lan ]; then
        check "endpoint scelto: LAN (NAS nella /24 del server)" grep -q '^Endpoint = 172.30.1.10:51820$' "$DIR/stato/mvpn0.conf"
    else
        check "endpoint scelto: pubblico (NAS fuori dalla LAN)" grep -q '^Endpoint = vpn.prova.test:51820$' "$DIR/stato/mvpn0.conf"
    fi
    check "handshake col server" aspetta 30 ha_handshake
    check "ping 10.66.0.1 attraverso la VPN" ping -c 2 -W 3 10.66.0.1
    check "rotta solo per 10.66.0.0/24" sh -c "ip -4 route show dev mvpn0 | grep -Eqx '10\.66\.0\.0/24 scope link *' && [ \$(ip -4 route show dev mvpn0 | wc -l) = 1 ]"
    check "rotta predefinita intatta" sh -c "ip -4 route show default | grep -qv mvpn0"
    check "script di avvio in /usr/local/etc/rc.d" test -x /usr/local/etc/rc.d/S99marshmellovpn.sh
    check "registro leggibile con la riga di accensione" grep -q "Tunnel acceso" "$LOG"
    check "stato dice tunnel acceso" sh -c "sh $DIR/marshmellovpn.sh stato | grep -q 'Tunnel: acceso'"
    fine
    ;;
ricreazione)
    p1=$(porta)
    righe=$(wc -l < "$LOG")
    echo "  porta locale prima: $p1; blocco l'UDP verso il server"
    iptables -A OUTPUT -p udp --dport 51820 -j DROP
    # Handshake più vecchio di MVPN_TIMEOUT_HANDSHAKE (40 s in prova, 200 s sul NAS) -> ricreato.
    check "tunnel ricreato per handshake vecchio" aspetta 90 sh -c "tail -n +$((righe + 1)) $LOG | grep -q 'Ultimo handshake .* s fa: tunnel ricreato.'"
    # La porta della prima riaccensione dopo il blocco (dal registro: il tunnel intanto può già essere ricreato di nuovo).
    p2=$(tail -n +$((righe + 1)) "$LOG" | sed -n 's/.*Tunnel acceso: .*porta locale \([0-9]*\)\..*/\1/p' | head -n 1)
    check "porta locale nuova dopo la ricreazione ($p1 -> $p2)" test -n "$p2" -a "$p2" != "$p1"
    # Nessuna risposta: prova l'altro endpoint (dalla LAN il pubblico non risponde: hairpin mancante).
    check "senza risposta prova l'altro endpoint" aspetta 40 sh -c "tail -n +$((righe + 1)) $LOG | grep -q 'Nessun contatto col server: provo'"
    iptables -D OUTPUT -p udp --dport 51820 -j DROP
    echo "  UDP sbloccato"
    check "di nuovo collegato" aspetta 60 sh -c "tail -n +$((righe + 1)) $LOG | grep -q 'Collegato al server'"
    check "ping 10.66.0.1 dopo la ricreazione" aspetta 20 ping -c 1 -W 2 10.66.0.1
    fine
    ;;
riavvio)
    ind=$(sed -n 's/^MVPN_INDIRIZZO=//p' "$DIR/stato/profilo.env")
    sh "$DIR/marshmellovpn.sh" ferma
    check "dopo «ferma» l'interfaccia non c'è più" sh -c "! ip link show mvpn0"
    check "dopo «ferma» il conf con la chiave privata è cancellato" test ! -f "$DIR/stato/mvpn0.conf"
    sh /usr/local/etc/rc.d/S99marshmellovpn.sh start
    check "dopo l'avvio di DSM il tunnel riparte" aspetta 30 ha_handshake
    check "stesso indirizzo dopo il riavvio ($ind)" sh -c "ip -4 address show dev mvpn0 | grep -q 'inet $ind'"
    check "«avvia» due volte non avvia un secondo supervisore" sh -c "sh $DIR/marshmellovpn.sh avvia | grep -q 'già attivo'"
    fine
    ;;
ping)
    check "ping $2 attraverso la VPN" aspetta 20 ping -c 1 -W 2 "$2"
    fine
    ;;
disinstalla)
    sh "$DIR/disinstalla-nas.sh"
    check "cartella cancellata" test ! -d "$DIR"
    check "script di avvio cancellato" test ! -f /usr/local/etc/rc.d/S99marshmellovpn.sh
    check "interfaccia tolta" sh -c "! ip link show mvpn0"
    fine
    ;;
*)
    echo "uso: $0 installa lan|pubblico | ricreazione | riavvio | ping IP | disinstalla" >&2
    exit 2
    ;;
esac
