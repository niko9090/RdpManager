# linuxserver/wireguard + wireguard-go (userspace) + iptables-legacy.
# Serve su DSM 7.1 (kernel 4.4, senza modulo wireguard e con nf_tables non funzionante):
# l'immagine ufficiale non include wireguard-go.
FROM lscr.io/linuxserver/wireguard:latest
RUN apk add --no-cache wireguard-go iptables-legacy
# Lo script di avvio dell'immagine si blocca (sleep infinity) se manca il modulo del kernel:
# lo sostituiamo con uno che prosegue e lascia fare a wireguard-go.
RUN printf '#!/usr/bin/with-contenv bash\nmkdir -p /config/templates /config/coredns\necho "Uname info: $(uname -a)"\necho "**** Modulo kernel assente: uso wireguard-go (userspace) ****"\n' > /etc/s6-overlay/s6-rc.d/init-wireguard-module/run
ENV WG_QUICK_USERSPACE_IMPLEMENTATION=wireguard-go
