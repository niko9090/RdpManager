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

1. Su **DSM 7.1** il pacchetto si chiama **Docker** (Container Manager esiste da DSM 7.2).
   Da SSH: `sudo /usr/syno/bin/synopkg install_from_server Docker`, poi `sudo /usr/syno/bin/synopkg start Docker`.
   Se l'installazione fallisce in "preinst" (impossibile creare la cartella condivisa `docker`),
   scarica l'.spk e installalo con `synopkg install`; in caso di errore crea prima la cartella con
   `sudo /usr/syno/sbin/synoshare --add docker "" /volume1/docker "" "" "" 0 0`.
2. Attiva SSH: **Pannello di controllo -> Terminale e SNMP -> Abilita servizio SSH**.
3. Collegati dal PC con un utente amministratore.

## 2. Copiare la cartella

Copia tutto il contenuto di `wireguard-synology` (`Dockerfile`, `docker-compose.yml`, `config/`, `scripts/`)
in `/volume1/docker/wireguard`. I file `.sh` devono avere fine riga LF.

## 3. Avvio

Il kernel 4.4 di DSM 7.1 non ha il modulo WireGuard e `nf_tables` non funziona: il `Dockerfile`
parte da `linuxserver/wireguard` e aggiunge **wireguard-go** (userspace) e **iptables-legacy**
(l'immagine ufficiale non li ha e si blocca all'avvio). Il pacchetto Docker di DSM 7.1 ha
`docker-compose` 1.28 (non `docker compose`):

```
cd /volume1/docker/wireguard
sudo env PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin COMPOSE_HTTP_TIMEOUT=600 /usr/local/bin/docker-compose up -d --build
sudo /usr/local/bin/docker exec wireguard sh /scripts/prima-generazione.sh
sudo /usr/local/bin/docker restart wireguard
sudo /usr/local/bin/docker exec wireguard wg show wg0
```

Il primo avvio senza `wg0.conf` e' normale; `prima-generazione.sh` crea `config/wg0.conf` dal
modello e stampa la chiave pubblica del server. Il container ha `restart: unless-stopped` e
il pacchetto Docker parte da solo all'avvio del NAS. `config/` contiene le chiavi del server
e i profili: e' escluso dal repository (`.gitignore`).

## 4. Router, firewall e DNS

1. **Router**: inoltra la porta **51820/UDP** (esterna e interna) verso **192.168.1.199**.
2. **Firewall DSM**: **Pannello di controllo -> Sicurezza -> Firewall**, aggiungi una regola
   che consenta la porta personalizzata **UDP 51820** da qualsiasi origine (o dai tuoi
   paesi) e che stia sopra la regola "Nega tutto".
3. **DNS**: `syncnetpro.com` deve puntare all'IP pubblico di casa. Se l'IP cambia, usa il
   DDNS (**Pannello di controllo -> Accesso esterno -> DDNS**) e un CNAME da
   `syncnetpro.com`/sottodominio verso il nome DDNS, oppure aggiorna il record A con il tuo gestore.

## 5. Aggiungere un PC

**Dalla 1.1 non serve fare niente sul Synology**: l'utente si registra (o accede) in RdpManager, il PC crea le sue
chiavi, il server `rdpm-sync` gli assegna il primo indirizzo libero, lo scrive in `wg0.conf` (in un blocco proprio)
e lo applica al tunnel con `wg set`, e l'app accende la VPN da sola. Come mettere in opera `rdpm-sync` (e perche'
questo `docker-compose.yml` monta la cartella `./run`): `../rdpm-sync/LEGGIMI.md`. Gli script qui sotto restano
per i casi a mano (un PC non gestito dall'app, o il server `rdpm-sync` non installato).

1. Sul PC apri RdpManager: **Strumenti -> VPN (WireGuard)...**.
2. Il client crea le sue chiavi e mostra un frammento `[Peer]` con la sua chiave
   pubblica e un indirizzo `10.66.0.N/32`. Copia la **chiave pubblica**.
3. Sul Synology:

```
sudo /usr/local/bin/docker exec wireguard sh /scripts/aggiungi-pc.sh NomePC "CHIAVE_PUBBLICA_DEL_PC"
```

   Facoltativo, terzo parametro, l'indirizzo voluto (es. `10.66.0.7`). Senza, lo script
   assegna il primo `.N` libero dal `.2`. Il comando ricarica con `wg syncconf` senza
   interrompere gli altri PC e stampa la chiave pubblica del server e l'endpoint.
4. Inserisci quei due dati nel pannello VPN di RdpManager e attiva la VPN.

Per fare tutto sul server, senza passare dal client (genera anche le chiavi del PC):

```
sudo /usr/local/bin/docker exec wireguard sh /scripts/crea-profilo-client.sh NomePC
sudo cat /volume1/docker/wireguard/config/profili/NomePC.conf
```

Il profilo (con la chiave privata del PC) resta in `config/profili/` (solo root) e va copiato
fuori dal repository.

Altri comandi:

```
sudo /usr/local/bin/docker exec wireguard sh /scripts/elenca-pc.sh
sudo /usr/local/bin/docker exec wireguard sh /scripts/rimuovi-pc.sh NomePC
```

## 6. Controlli

- Sul Synology: `sudo /usr/local/bin/docker exec wireguard wg show` -> deve comparire il PC con un
  "latest handshake" di pochi secondi dopo l'attivazione.
- Dal PC con la VPN attiva: `ping 10.66.0.1`.
- Se non risponde: controlla porta UDP sul router, regola del firewall DSM, DNS di
  `syncnetpro.com`, e che le chiavi pubbliche coincidano.
- Per raggiungere anche PC della LAN senza client (192.168.1.0/24) servono le righe
  `PostUp`/`PostDown` di `wg0.conf` (gia' presenti, con NAT verso la LAN) e che il
  client abbia `192.168.1.0/24` tra gli indirizzi permessi.

## 6b. Server di sincronizzazione

Il `docker-compose.yml` pubblica `127.0.0.1:8787:8787`: la porta serve al contenitore `rdpm-sync`
(`server/rdpm-sync`), che usa la rete di questo contenitore e risponde ai client VPN su
`http://10.66.0.1:8787`. Se ricrei questo contenitore, ricrea anche `rdpm-sync` (vedi il suo LEGGIMI).

## 7. La DLL wireguard.dll nel client

Il client RdpManager **non** usa `tunnel.dll` (WireGuard non la pubblica gia' compilata e firmata):
parla direttamente con il driver **wireguard-nt**, tramite il suo `wireguard.dll` ufficiale, da un
servizio Windows fornito da `RdpManager.exe` stesso. Serve quindi un solo file, gia' incluso nel
repository in `src/RdpManager/Vpn/dll/amd64/wireguard.dll` (wireguard-nt 1.1, firmato WireGuard LLC)
e copiato accanto a `RdpManager.exe` e nell'installer.

Per aggiornarlo:

1. Scarica <https://download.wireguard.com/wireguard-nt/wireguard-nt-1.1.zip> (o la versione
   nuova dalla stessa cartella) in una cartella a parte ed estrailo.
2. Prendi `bin/amd64/wireguard.dll` e copialo in `src/RdpManager/Vpn/dll/amd64/`.
3. **Verifica la firma**: tasto destro sul file -> Proprieta' -> Firme digitali, deve
   comparire "WireGuard LLC" e la firma valida. Oppure in PowerShell:

```
Get-AuthenticodeSignature .\wireguard.dll | Format-List Status, SignerCertificate
```

   `Status` deve essere `Valid`. Se non lo e', non usare il file.

La prima volta che accendi la VPN Windows chiede una conferma UAC (installa il servizio
`RdpManagerVpn`); poi accendere e spegnere non chiedono piu' nulla, finche' non cambi la
configurazione (chiavi, indirizzo, server).
