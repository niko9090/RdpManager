#!/bin/sh
# marshmelloVPN sul NAS: accende il tunnel WireGuard (wireguard-go in spazio utente + wg) verso il server della VPN
# e lo tiene vivo. Lo installa installa-nas.sh nella cartella persistente (predefinita /volume1/@marshmellovpn).
#
#   sh marshmellovpn.sh avvia        avvia il supervisore in background (se non è già attivo)
#   sh marshmellovpn.sh ferma        ferma supervisore e tunnel
#   sh marshmellovpn.sh riavvia
#   sh marshmellovpn.sh stato        stato del tunnel, ultimo handshake, ultime righe del registro
#   sh marshmellovpn.sh registra ... registra di nuovo il NAS sul server (parametri di mvpn-helper registra)
#   sh marshmellovpn.sh log          ultime 50 righe del registro
#   sh marshmellovpn.sh supervisore  il ciclo, in primo piano (lo usa «avvia»; utile per le prove)
#
# Il supervisore fa lo stesso lavoro del servizio Windows (src/RdpManager/Vpn/VpnServiceLoop.cs): sceglie l'endpoint
# LAN se il NAS sta nella /24 del server, altrimenti quello pubblico; se non arriva nessun handshake prova l'altro
# endpoint; se l'ultimo handshake è più vecchio di 200 s (o non c'è dopo 2 minuti) ricrea il tunnel, che così riparte
# da una porta nuova e risolve di nuovo il nome del server.
# Tocca solo la propria interfaccia (mvpn0) e la rotta della rete VPN: niente Tailscale, niente firewall, niente DNS.

PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH

MVPN_DIR=${MVPN_DIR:-$(cd "$(dirname "$0")" && pwd)}
# Impostazioni facoltative (stesse variabili qui sotto), per esempio un MTU diverso.
if [ -f "$MVPN_DIR/impostazioni.env" ]; then
    # shellcheck disable=SC1091
    . "$MVPN_DIR/impostazioni.env"
fi
MVPN_INTERFACCIA=${MVPN_INTERFACCIA:-mvpn0}
MVPN_MTU=${MVPN_MTU:-1420}
MVPN_PASSO=${MVPN_PASSO:-5}                         # secondi fra un controllo e l'altro
MVPN_TIMEOUT_HANDSHAKE=${MVPN_TIMEOUT_HANDSHAKE:-200} # handshake più vecchio = tunnel morto (VpnServiceLoop.HandshakeTimeout)
MVPN_TIMEOUT_PRIMO=${MVPN_TIMEOUT_PRIMO:-120}       # nessun handshake dall'accensione (FirstHandshakeTimeout)
MVPN_TIMEOUT_CAMBIO=${MVPN_TIMEOUT_CAMBIO:-20}      # senza contatto si prova l'altro endpoint (VpnEndpointChoice.SwitchTimeout)
MVPN_RITARDO=${MVPN_RITARDO:-15}                    # dopo un'accensione fallita (RetryDelay)
MVPN_RITARDO_REGISTRAZIONE=${MVPN_RITARDO_REGISTRAZIONE:-60}
MVPN_RUN_WG=${MVPN_RUN_WG:-/var/run/wireguard}      # dove wireguard-go mette il socket (lo cerca anche wg)
MVPN_BIN=${MVPN_BIN:-$MVPN_DIR/bin}
MVPN_STATO=${MVPN_STATO:-$MVPN_DIR/stato}
MVPN_LOG=${MVPN_LOG:-$MVPN_DIR/log/marshmellovpn.log}
MVPN_RUN=${MVPN_RUN:-$MVPN_DIR/run}
MVPN_LOG_MAX=${MVPN_LOG_MAX:-1048576}

# Stato del supervisore (come i campi di VpnServiceLoop).
S_USA_LAN=""      # "" = da scegliere, 1 = LAN, 0 = pubblico
S_RISPOSTO=0      # il tunnel attuale ha avuto almeno un handshake
S_TENTATIVO=0     # quando è stato acceso il tunnel attuale
S_PROSSIMO=0      # prima di questo momento non si riprova ad accendere
S_RICREAZIONI=0
S_ENDPOINT=""

# ---------------------------------------------------------------- funzioni pure (provate da test/test-unita.sh)

adesso() { date +%s; }

registra_riga() {
    mkdir -p "$(dirname "$MVPN_LOG")"
    printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$MVPN_LOG"
}

ruota_log() {
    [ -f "$MVPN_LOG" ] || return 0
    dim=$(wc -c < "$MVPN_LOG" 2>/dev/null || echo 0)
    if [ "$dim" -gt "$MVPN_LOG_MAX" ]; then
        mv -f "$MVPN_LOG" "$MVPN_LOG.1"
        registra_riga "Registro ruotato (il precedente è in $(basename "$MVPN_LOG").1)."
    fi
}

# Indirizzi IPv4 delle schede di questo NAS (uno per riga), senza loopback né l'interfaccia della VPN.
ipv4_locali() {
    ip -4 -o address show 2>/dev/null | awk -v vpn="$MVPN_INTERFACCIA" '$2 != "lo" && $2 != vpn { split($4, a, "/"); print a[1] }'
}

# su_lan_del_server ENDPOINT_LAN "INDIRIZZI": vero se uno degli indirizzi sta nella stessa /24 dell'IP del server
# (come VpnEndpointChoice.OnServerLan). L'endpoint LAN deve essere IPv4:porta.
su_lan_del_server() {
    host=${1%:*}
    case "$host" in
        *[!0-9.]* | "" ) return 1 ;;
    esac
    # quattro numeri separati da punti
    case "$host" in
        *.*.*.*.* ) return 1 ;;
        *.*.*.* ) ;;
        * ) return 1 ;;
    esac
    rete=${host%.*}
    for a in $2; do
        case "$a" in 127.*) continue ;; esac
        [ "${a%.*}" = "$rete" ] && return 0
    done
    return 1
}

# Endpoint da usare: quello LAN se scelto (e se c'è), altrimenti il pubblico (VpnEndpointChoice.Apply).
scegli_endpoint() {
    if [ "$S_USA_LAN" = 1 ] && [ -n "${MVPN_ENDPOINT_LAN:-}" ]; then
        printf '%s\n' "$MVPN_ENDPOINT_LAN"
    else
        printf '%s\n' "$MVPN_ENDPOINT"
    fi
}

# scrivi_conf FILE ENDPOINT: configurazione per «wg setconf» (niente Address/DNS: quelli li fa ip, e il DNS non si tocca).
# Senza ListenPort: a ogni ricreazione wireguard-go prende una porta nuova.
scrivi_conf() {
    chiave=$(cat "$MVPN_STATO/privata.key") || return 1
    vecchia=$(umask)
    umask 077
    {
        echo "# Generato da marshmellovpn.sh: contiene la chiave privata del NAS, non copiarlo."
        echo "[Interface]"
        echo "PrivateKey = $chiave"
        echo ""
        echo "[Peer]"
        echo "PublicKey = $MVPN_CHIAVE_SERVER"
        echo "AllowedIPs = $MVPN_ALLOWED_IPS"
        echo "Endpoint = $2"
        echo "PersistentKeepalive = $MVPN_KEEPALIVE"
    } > "$1"
    umask "$vecchia"
    chmod 600 "$1"
}

# valuta ORA HANDSHAKE LETTO: decide cosa fare del tunnel acceso (come VpnServiceLoop.Tick).
# HANDSHAKE = secondi unix dell'ultimo handshake (0 = mai), LETTO = 1 se «wg show» ha risposto.
# Imposta AZIONE (ok | ricrea | cambia) e MOTIVO.
valuta() {
    ora=$1; hs=$2; letto=$3
    AZIONE=ok; MOTIVO=""
    if [ "$letto" != 1 ]; then
        AZIONE=ricrea; MOTIVO="Il tunnel non risponde: ricreato."
        return 0
    fi
    if [ "$hs" -gt 0 ]; then
        S_RISPOSTO=1
        eta=$((ora - hs))
        if [ "$eta" -gt "$MVPN_TIMEOUT_HANDSHAKE" ]; then
            AZIONE=ricrea; MOTIVO="Ultimo handshake $eta s fa: tunnel ricreato."
        fi
        return 0
    fi
    da=$((ora - S_TENTATIVO))
    if [ -n "${MVPN_ENDPOINT_LAN:-}" ]; then
        if [ "$S_RISPOSTO" = 0 ] && [ "$da" -gt "$MVPN_TIMEOUT_CAMBIO" ]; then
            # Nessun contatto: tipico dell'hairpin NAT mancante (dalla LAN il nome pubblico non risponde); si prova l'altro.
            AZIONE=cambia
            if [ "$S_USA_LAN" = 1 ]; then MOTIVO="Nessun contatto col server: provo l'endpoint pubblico."
            else MOTIVO="Nessun contatto col server: provo l'endpoint LAN."; fi
        fi
    elif [ "$da" -gt "$MVPN_TIMEOUT_PRIMO" ]; then
        AZIONE=ricrea; MOTIVO="Nessun handshake dall'accensione: tunnel ricreato."
    fi
    return 0
}

carica_profilo() {
    [ -f "$MVPN_STATO/profilo.env" ] && [ -f "$MVPN_STATO/privata.key" ] || return 1
    # Il file lo scrive mvpn-helper con valori già controllati (niente spazi, virgolette o caratteri speciali).
    # shellcheck disable=SC1091
    . "$MVPN_STATO/profilo.env"
    [ -n "${MVPN_INDIRIZZO:-}" ] && [ -n "${MVPN_ENDPOINT:-}" ] && [ -n "${MVPN_CHIAVE_SERVER:-}" ] || return 1
    MVPN_RETE=${MVPN_RETE:-10.66.0.0/24}
    MVPN_ALLOWED_IPS=${MVPN_ALLOWED_IPS:-$MVPN_RETE}
    MVPN_KEEPALIVE=${MVPN_KEEPALIVE:-25}
    MVPN_ENDPOINT_LAN=${MVPN_ENDPOINT_LAN:-}
    return 0
}

# ---------------------------------------------------------------- tunnel

pid_vivo() { [ -n "$1" ] && kill -0 "$1" 2>/dev/null; }
leggi_pid() { cat "$1" 2>/dev/null || true; }

tunnel_attivo() {
    pid_vivo "$(leggi_pid "$MVPN_RUN/wireguard-go.pid")" && ip link show "$MVPN_INTERFACCIA" >/dev/null 2>&1
}

wg_cmd() { WG_HIDE_KEYS=always "$MVPN_BIN/wg" "$@"; }

# Imposta HS (0 = mai) e LETTO (1 se wg ha risposto).
leggi_handshake() {
    HS=0; LETTO=0
    uscita=$(wg_cmd show "$MVPN_INTERFACCIA" latest-handshakes 2>/dev/null) || return 0
    LETTO=1
    v=$(printf '%s\n' "$uscita" | awk -v k="$MVPN_CHIAVE_SERVER" '$1 == k { print $2; exit }')
    case "$v" in ''|*[!0-9]*) v=0 ;; esac
    HS=$v
}

assicura_tun() {
    [ -c /dev/net/tun ] && return 0
    # DSM: il modulo c'è ma non sempre è caricato; il nodo a volte manca.
    if [ -f /lib/modules/tun.ko ]; then insmod /lib/modules/tun.ko 2>/dev/null || true; fi
    modprobe tun 2>/dev/null || true
    if [ ! -e /dev/net/tun ]; then
        mkdir -p /dev/net
        mknod /dev/net/tun c 10 200 2>/dev/null || true
        chmod 600 /dev/net/tun 2>/dev/null || true
    fi
    [ -c /dev/net/tun ]
}

ferma_tunnel() {
    pid=$(leggi_pid "$MVPN_RUN/wireguard-go.pid")
    if pid_vivo "$pid"; then
        kill "$pid" 2>/dev/null || true
        i=0
        while pid_vivo "$pid" && [ $i -lt 30 ]; do sleep 0.1 2>/dev/null || sleep 1; i=$((i + 1)); done
        pid_vivo "$pid" && kill -9 "$pid" 2>/dev/null
    fi
    rm -f "$MVPN_RUN/wireguard-go.pid"
    ip link delete "$MVPN_INTERFACCIA" >/dev/null 2>&1 || true
    rm -f "$MVPN_RUN_WG/$MVPN_INTERFACCIA.sock" "$MVPN_STATO/$MVPN_INTERFACCIA.conf"
}

# Se manca il profilo (installazione interrotta, cartella «stato» ripristinata a metà) prova a registrarsi di nuovo
# con l'account salvato.
riprova_registrazione() {
    [ -f "$MVPN_STATO/account.json" ] || { registra_riga "Manca il profilo e il NAS non è registrato: rilancia installa-nas.sh."; return 1; }
    registra_riga "Manca il profilo: provo a registrare di nuovo il NAS con l'account salvato."
    "$MVPN_BIN/mvpn-helper" registra --stato "$MVPN_STATO" >> "$MVPN_LOG" 2>&1
}

# avvia_tunnel ORA: accende il tunnel (VpnServiceLoop.TryStart). Su errore ferma tutto e riprova dopo MVPN_RITARDO.
avvia_tunnel() {
    ora=$1
    if ! carica_profilo; then
        if ! riprova_registrazione || ! carica_profilo; then
            S_PROSSIMO=$((ora + MVPN_RITARDO_REGISTRAZIONE))
            return 1
        fi
    fi
    if [ -z "$S_USA_LAN" ]; then
        if [ -n "$MVPN_ENDPOINT_LAN" ] && su_lan_del_server "$MVPN_ENDPOINT_LAN" "$(ipv4_locali)"; then S_USA_LAN=1; else S_USA_LAN=0; fi
    fi
    S_ENDPOINT=$(scegli_endpoint)
    if ! accendi "$S_ENDPOINT"; then
        registra_riga "Accensione non riuscita, riprovo fra $MVPN_RITARDO s: $ERRORE"
        ferma_tunnel
        S_PROSSIMO=$((ora + MVPN_RITARDO))
        return 1
    fi
    S_TENTATIVO=$ora
    S_RISPOSTO=0
    if [ "$S_USA_LAN" = 1 ]; then tipo="LAN"; else tipo="pubblico"; fi
    porta=$(wg_cmd show "$MVPN_INTERFACCIA" listen-port 2>/dev/null || echo "?")
    registra_riga "Tunnel acceso: $MVPN_INDIRIZZO, endpoint $tipo $S_ENDPOINT, porta locale $porta."
    return 0
}

accendi() {
    ERRORE=""
    ferma_tunnel
    assicura_tun || { ERRORE="/dev/net/tun non disponibile (modulo tun del kernel)"; return 1; }
    mkdir -p "$MVPN_RUN" "$MVPN_RUN_WG"
    WG_PROCESS_FOREGROUND=1 "$MVPN_BIN/wireguard-go" -f "$MVPN_INTERFACCIA" >> "$MVPN_LOG" 2>&1 &
    echo $! > "$MVPN_RUN/wireguard-go.pid"
    i=0
    while [ ! -S "$MVPN_RUN_WG/$MVPN_INTERFACCIA.sock" ]; do
        i=$((i + 1))
        if [ $i -gt 100 ] || ! pid_vivo "$(leggi_pid "$MVPN_RUN/wireguard-go.pid")"; then
            ERRORE="wireguard-go non è partito (guarda le righe qui sopra)"; return 1
        fi
        sleep 0.1 2>/dev/null || sleep 1
    done
    conf="$MVPN_STATO/$MVPN_INTERFACCIA.conf"
    scrivi_conf "$conf" "$1" || { ERRORE="non riesco a scrivere $conf"; return 1; }
    # wg risolve qui il nome dell'endpoint: a ogni ricreazione si risolve di nuovo.
    out=$(wg_cmd setconf "$MVPN_INTERFACCIA" "$conf" 2>&1) || { ERRORE="wg setconf: $out (nome del server non risolto?)"; return 1; }
    out=$(ip -4 address add "$MVPN_INDIRIZZO" dev "$MVPN_INTERFACCIA" 2>&1) || { ERRORE="ip address: $out"; return 1; }
    out=$(ip link set dev "$MVPN_INTERFACCIA" mtu "$MVPN_MTU" up 2>&1) || { ERRORE="ip link: $out"; return 1; }
    # Solo la rete della VPN passa dal tunnel: la LAN del NAS, Internet e Tailscale restano come sono.
    out=$(ip -4 route replace "$MVPN_RETE" dev "$MVPN_INTERFACCIA" 2>&1) || { ERRORE="ip route: $out"; return 1; }
    return 0
}

# Un passo del ciclo (VpnServiceLoop.Tick).
passo() {
    ora=$(adesso)
    if ! tunnel_attivo; then
        if [ "$S_TENTATIVO" -gt 0 ] && [ "$ora" -ge "$S_PROSSIMO" ] && [ -f "$MVPN_RUN/wireguard-go.pid" ]; then
            registra_riga "wireguard-go si è fermato: riaccendo il tunnel."
        fi
        [ "$ora" -ge "$S_PROSSIMO" ] && avvia_tunnel "$ora"
        return 0
    fi
    leggi_handshake
    valuta "$ora" "$HS" "$LETTO"
    case "$AZIONE" in
        ricrea)
            registra_riga "$MOTIVO"
            S_RICREAZIONI=$((S_RICREAZIONI + 1))
            S_USA_LAN="" # la rete può essere cambiata: si sceglie di nuovo l'endpoint
            avvia_tunnel "$ora"
            ;;
        cambia)
            registra_riga "$MOTIVO"
            S_RICREAZIONI=$((S_RICREAZIONI + 1))
            if [ "$S_USA_LAN" = 1 ]; then S_USA_LAN=0; else S_USA_LAN=1; fi
            avvia_tunnel "$ora"
            ;;
    esac
    if [ "$S_RISPOSTO" = 1 ] && [ ! -f "$MVPN_RUN/collegato" ]; then
        : > "$MVPN_RUN/collegato"
        registra_riga "Collegato al server (handshake riuscito, endpoint $S_ENDPOINT)."
    elif [ "$S_RISPOSTO" = 0 ]; then
        rm -f "$MVPN_RUN/collegato"
    fi
    return 0
}

# ---------------------------------------------------------------- comandi

serve_root() {
    if [ "$(id -u)" != 0 ]; then
        echo "Serve l'amministratore: lancia con sudo (sudo sh $0 $1)." >&2
        exit 1
    fi
}

supervisore_attivo() { pid_vivo "$(leggi_pid "$MVPN_RUN/supervisore.pid")"; }

cmd_supervisore() {
    mkdir -p "$MVPN_RUN"
    if supervisore_attivo && [ "$(leggi_pid "$MVPN_RUN/supervisore.pid")" != "$$" ]; then
        echo "Il supervisore è già attivo." >&2
        exit 0
    fi
    echo $$ > "$MVPN_RUN/supervisore.pid"
    rm -f "$MVPN_RUN/collegato"
    trap 'registra_riga "Supervisore fermato."; ferma_tunnel; rm -f "$MVPN_RUN/supervisore.pid" "$MVPN_RUN/collegato"; exit 0' TERM INT HUP
    registra_riga "Supervisore avviato (pid $$, interfaccia $MVPN_INTERFACCIA)."
    while :; do
        ruota_log
        passo
        sleep "$MVPN_PASSO" &
        wait $! 2>/dev/null || true
    done
}

cmd_avvia() {
    mkdir -p "$MVPN_RUN" "$(dirname "$MVPN_LOG")"
    if supervisore_attivo; then
        echo "marshmelloVPN è già attivo (supervisore pid $(leggi_pid "$MVPN_RUN/supervisore.pid"))."
        return 0
    fi
    if command -v setsid >/dev/null 2>&1; then
        setsid /bin/sh "$MVPN_DIR/marshmellovpn.sh" supervisore < /dev/null > /dev/null 2>&1 &
    else
        nohup /bin/sh "$MVPN_DIR/marshmellovpn.sh" supervisore < /dev/null > /dev/null 2>&1 &
    fi
    i=0
    while ! supervisore_attivo && [ $i -lt 50 ]; do sleep 0.1 2>/dev/null || sleep 1; i=$((i + 1)); done
    if supervisore_attivo; then
        echo "marshmelloVPN avviato (registro: $MVPN_LOG)."
    else
        echo "marshmelloVPN non è partito: guarda $MVPN_LOG." >&2
        return 1
    fi
}

cmd_ferma() {
    pid=$(leggi_pid "$MVPN_RUN/supervisore.pid")
    if pid_vivo "$pid"; then
        kill "$pid" 2>/dev/null || true
        i=0
        while pid_vivo "$pid" && [ $i -lt 100 ]; do sleep 0.1 2>/dev/null || sleep 1; i=$((i + 1)); done
        pid_vivo "$pid" && kill -9 "$pid" 2>/dev/null
    fi
    rm -f "$MVPN_RUN/supervisore.pid" "$MVPN_RUN/collegato"
    ferma_tunnel
    echo "marshmelloVPN fermato."
}

cmd_stato() {
    if supervisore_attivo; then
        echo "Supervisore: attivo (pid $(leggi_pid "$MVPN_RUN/supervisore.pid"))"
    else
        echo "Supervisore: fermo"
    fi
    if carica_profilo; then
        echo "Nome nella VPN: ${MVPN_NOME:-?}   indirizzo: $MVPN_INDIRIZZO"
        echo "Endpoint pubblico: $MVPN_ENDPOINT   endpoint LAN: ${MVPN_ENDPOINT_LAN:-nessuno}"
    else
        echo "Profilo: mancante (il NAS non è registrato: rilancia installa-nas.sh)"
    fi
    if tunnel_attivo; then
        leggi_handshake
        if [ "$HS" -gt 0 ]; then
            echo "Tunnel: acceso, ultimo handshake $(( $(adesso) - HS )) s fa"
        else
            echo "Tunnel: acceso, nessun handshake per ora"
        fi
        wg_cmd show "$MVPN_INTERFACCIA" 2>/dev/null | sed 's/^/  /'
    else
        echo "Tunnel: spento"
    fi
    if [ -f "$MVPN_LOG" ]; then
        echo "Ultime righe del registro ($MVPN_LOG):"
        tail -n 8 "$MVPN_LOG" | sed 's/^/  /'
    fi
}

main() {
    comando=${1:-}
    [ $# -gt 0 ] && shift
    case "$comando" in
        avvia|start) serve_root avvia; cmd_avvia ;;
        ferma|stop) serve_root ferma; cmd_ferma ;;
        riavvia|restart) serve_root riavvia; cmd_ferma; cmd_avvia ;;
        stato|status) serve_root stato; cmd_stato ;;
        supervisore) serve_root supervisore; cmd_supervisore ;;
        registra) serve_root registra; "$MVPN_BIN/mvpn-helper" registra --stato "$MVPN_STATO" "$@" ;;
        log) tail -n 50 "$MVPN_LOG" ;;
        *)
            sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
            [ -z "$comando" ] || exit 2
            ;;
    esac
}

# Con MVPN_SOLO_FUNZIONI=1 (le prove) si caricano solo le funzioni.
if [ "${MVPN_SOLO_FUNZIONI:-0}" != 1 ]; then
    main "$@"
fi
