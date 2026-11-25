#!/bin/sh
# Server WireGuard FINTO per la prova end-to-end (contenitore «wgserver»): stessi programmi del pacchetto per NAS
# (wireguard-go e wg amd64), rete 10.66.0.0/24, server 10.66.0.1, porta 51820, come il server vero sul Synology.
# Scrive /vpn/wg0.conf e /vpn/server.pub, che rdpm-sync usa per aggiungere i NAS con «wg set».
set -eu
apk add --no-cache -q iproute2 >/dev/null
BIN=/pkg/bin/amd64
mkdir -p /vpn /var/run/wireguard
umask 077
if [ ! -f /vpn/server.key ]; then
    "$BIN/wg" genkey > /vpn/server.key
fi
"$BIN/wg" pubkey < /vpn/server.key > /vpn/server.pub
chmod 644 /vpn/server.pub
if [ ! -f /vpn/wg0.conf ]; then
    cat > /vpn/wg0.conf <<EOF
[Interface]
Address = 10.66.0.1/24
ListenPort = 51820
PrivateKey = $(cat /vpn/server.key)
EOF
fi
printf '[Interface]\nPrivateKey = %s\nListenPort = 51820\n' "$(cat /vpn/server.key)" > /tmp/setconf.conf
WG_PROCESS_FOREGROUND=1 "$BIN/wireguard-go" -f wg0 &
i=0
while [ ! -S /var/run/wireguard/wg0.sock ]; do i=$((i + 1)); [ $i -lt 100 ] || exit 1; sleep 0.1; done
"$BIN/wg" setconf wg0 /tmp/setconf.conf
ip address add 10.66.0.1/24 dev wg0
ip link set dev wg0 mtu 1420 up
# Come il server vero: inoltro fra i peer (NAS <-> PC).
echo 1 > /proc/sys/net/ipv4/ip_forward 2>/dev/null || true
echo "server WireGuard pronto: $(cat /vpn/server.pub)"
: > /vpn/pronto
wait
