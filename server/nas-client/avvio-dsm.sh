#!/bin/sh
# Avvio automatico di marshmelloVPN sul NAS Synology. installa-nas.sh lo copia come
# /usr/local/etc/rc.d/S99marshmellovpn.sh: all'accensione DSM lancia come root «S99marshmellovpn.sh start»,
# allo spegnimento «stop». La riga MVPN_DIR la scrive installa-nas.sh.
MVPN_DIR=/volume1/@marshmellovpn
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH

case "${1:-}" in
    start)
        # Non blocca l'avvio del NAS: aspetta in background che il volume sia montato (fino a 10 minuti).
        (
            i=0
            while [ ! -f "$MVPN_DIR/marshmellovpn.sh" ] && [ $i -lt 120 ]; do sleep 5; i=$((i + 1)); done
            if [ -f "$MVPN_DIR/marshmellovpn.sh" ]; then
                /bin/sh "$MVPN_DIR/marshmellovpn.sh" avvia
            else
                echo "$(date '+%Y-%m-%d %H:%M:%S') $MVPN_DIR non trovato: marshmelloVPN non avviato." >> /var/log/marshmellovpn-avvio.log
            fi
        ) < /dev/null > /dev/null 2>&1 &
        ;;
    stop)
        [ -f "$MVPN_DIR/marshmellovpn.sh" ] && /bin/sh "$MVPN_DIR/marshmellovpn.sh" ferma
        ;;
    restart)
        [ -f "$MVPN_DIR/marshmellovpn.sh" ] && /bin/sh "$MVPN_DIR/marshmellovpn.sh" riavvia
        ;;
    status)
        [ -f "$MVPN_DIR/marshmellovpn.sh" ] && /bin/sh "$MVPN_DIR/marshmellovpn.sh" stato
        ;;
    *)
        echo "Uso: $0 start|stop|restart|status"
        ;;
esac
exit 0
