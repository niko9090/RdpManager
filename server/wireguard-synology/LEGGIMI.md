# Server WireGuard su Synology per RdpManager

Questa cartella contiene tutto il necessario per far girare il server VPN
WireGuard sul Synology (DSM, IP in LAN 192.168.1.199) con Container Manager.

Dati della rete (devono coincidere con il client RdpManager):

| Voce | Valore |
|---|---|
| Rete VPN | 10.66.0.0/24 |
| Server | 10.66.0.1 |
| PC | 10.66.0.2 e successivi |
| Endpoint pubblico | syncnetpro.com:51820 (UDP) |
| Keepalive | 25 secondi |

## 1. Prerequisiti

1. In DSM apri **Centro pacchetti** e installa **Container Manager**.
2. Attiva SSH: **Pannello di controllo -> Terminale e SNMP -> Abilita servizio SSH**.
3. Collegati dal PC: `ssh tuoutente@192.168.1.199` (serve un utente amministratore).

## 2. Copiare la cartella

1. Con **File Station** crea la cartella condivisa `docker/wireguard` (o una a tua scelta).
2. Copia dentro tutto il contenuto di `wireguard-synology`: `docker-compose.yml`,
   `config/` e `scripts/`.
3. I file `.sh` devono avere fine riga LF (nel repository e' garantito da `.gitattributes`).
   Se li modifichi con Blocco note, salvali con fine riga Unix.

## 3. Avvio

Da SSH, nella cartella copiata (ad esempio `/volume1/docker/wireguard`):

```
cd /volume1/docker/wireguard
sudo docker compose up -d
sudo docker logs wireguard
```

Il primo avvio non trova ancora `wg0.conf`: e' normale. Genera le chiavi:

```
sudo docker exec wireguard sh /scripts/prima-generazione.sh
sudo docker restart wireguard
```

Lo script crea `config/wg0.conf` dal modello e stampa la chiave pubblica del server.

### Se su DSM manca il modulo del kernel WireGuard

L'immagine `linuxserver/wireguard` ripiega da sola su **wireguard-go** (versione
in userspace): nei log vedrai `wireguard-go` invece del modulo. Funziona, un po'
piu' lenta ma adatta a un uso con Desktop Remoto.
Se il container non parte nemmeno cosi' (per esempio `/dev/net/tun` assente), crea il
file all'avvio del NAS con un'attivita' pianificata (utente root, "Attivita' di avvio"):

```
modprobe tun 2>/dev/null; [ -c /dev/net/tun ] || { mkdir -p /dev/net; mknod /dev/net/tun c 10 200; chmod 600 /dev/net/tun; }
```

Alternativa se proprio non va: una **VM Linux leggera** (Debian minimale, 512 MB di RAM,
da Virtual Machine Manager) con `apt install wireguard`, la stessa `wg0.conf` in
`/etc/wireguard/` e `systemctl enable --now wg-quick@wg0`. Gli script `.sh` funzionano
uguali (impostando `WG_DIR=/etc/wireguard`). L'inoltro verso la LAN richiede
`net.ipv4.ip_forward=1` nella VM.

## 4. Router, firewall e DNS

1. **Router**: inoltra la porta **51820/UDP** (esterna e interna) verso **192.168.1.199**.
2. **Firewall DSM**: **Pannello di controllo -> Sicurezza -> Firewall**, aggiungi una regola
   che consenta la porta personalizzata **UDP 51820** da qualsiasi origine (o dai tuoi
   paesi) e che stia sopra la regola "Nega tutto".
3. **DNS**: `syncnetpro.com` deve puntare all'IP pubblico di casa. Se l'IP cambia, usa il
   DDNS (**Pannello di controllo -> Accesso esterno -> DDNS**) e un CNAME da
   `syncnetpro.com`/sottodominio verso il nome DDNS, oppure aggiorna il record A con il tuo gestore.

## 5. Aggiungere un PC

1. Sul PC apri RdpManager: **Strumenti -> VPN (WireGuard)...**.
2. Il client crea le sue chiavi e mostra un frammento `[Peer]` con la sua chiave
   pubblica e un indirizzo `10.66.0.N/32`. Copia la **chiave pubblica**.
3. Sul Synology:

```
sudo docker exec wireguard sh /scripts/aggiungi-pc.sh NomePC "CHIAVE_PUBBLICA_DEL_PC"
```

   Facoltativo, terzo parametro, l'indirizzo voluto (es. `10.66.0.7`). Senza, lo script
   assegna il primo `.N` libero dal `.2`. Il comando ricarica con `wg syncconf` senza
   interrompere gli altri PC e stampa la chiave pubblica del server e l'endpoint.
4. Inserisci quei due dati nel pannello VPN di RdpManager e attiva la VPN.

Altri comandi:

```
sudo docker exec wireguard sh /scripts/elenca-pc.sh
sudo docker exec wireguard sh /scripts/rimuovi-pc.sh NomePC
```

## 6. Controlli

- Sul Synology: `sudo docker exec wireguard wg show` -> deve comparire il PC con un
  "latest handshake" di pochi secondi dopo l'attivazione.
- Dal PC con la VPN attiva: `ping 10.66.0.1`.
- Se non risponde: controlla porta UDP sul router, regola del firewall DSM, DNS di
  `syncnetpro.com`, e che le chiavi pubbliche coincidano.
- Per raggiungere anche PC della LAN senza client (192.168.1.0/24) servono le righe
  `PostUp`/`PostDown` di `wg0.conf` (gia' presenti, con NAT verso la LAN) e che il
  client abbia `192.168.1.0/24` tra gli indirizzi permessi.

## 7. I file tunnel.dll e wireguard.dll nel client

Il client RdpManager include il servizio tunnel di WireGuard per Windows. Servono due
DLL, che **non** sono nel repository e vanno scaricate dal sito ufficiale:

1. Da <https://download.wireguard.com/wireguard-nt/> e dal pacchetto
   **embeddable-dll-service**: <https://git.zx2c4.com/wireguard-windows/about/embeddable-dll-service/>
   (le versioni compilate sono su <https://download.wireguard.com/windows-client/>; cerca
   il pacchetto `wireguard-embeddable-dll-service-*.zip`).
2. Estrai lo zip in una cartella a parte e prendi dalla cartella **amd64** i file
   `tunnel.dll` e `wireguard.dll`.
3. Copiali in `src/RdpManager/Vpn/dll/amd64/`.
4. **Verifica la firma**: tasto destro sul file -> Proprieta' -> Firme digitali, deve
   comparire "WireGuard LLC" e la firma valida. Oppure in PowerShell:

```
Get-AuthenticodeSignature .\tunnel.dll, .\wireguard.dll | Format-List Status, SignerCertificate
```

   `Status` deve essere `Valid`. Se non lo e', non usare i file.
