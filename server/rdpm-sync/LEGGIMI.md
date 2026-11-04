# rdpm-sync — server di sincronizzazione di RdpManager (e registro della VPN)

Un piccolo server (ASP.NET Core, .NET 8, SQLite, nessun servizio esterno) con account e login. Chi entra con
**lo stesso account** vede le stesse connessioni, cartelle e profili di credenziali in RdpManager. Dalla 1.1 è anche
il **registro della VPN**: l'utente si registra dall'app, il suo PC chiede la VPN e il server lo aggiunge a WireGuard
da solo (nessuno script da lanciare sul Synology).

**Il server non vede mai niente in chiaro**: RdpManager cifra il documento (formato `.rdpm`) sul PC, con una
chiave ricavata dalla password dell'account, e manda al server solo il blocco cifrato. Anche la password non
arriva mai al server: arriva un segreto derivato da essa. Se si perde la password dell'account, i dati non si
recuperano (il server non può aiutare). La **chiave privata WireGuard** nasce e resta sul PC: al server va solo la
chiave pubblica. Il contratto per chi scrive il cliente è in `docs/sync-server-api.md`.

## Come sta insieme sul Synology (DSM 7.1.1)

- Il kernel 4.4 di DSM 7.1 non ha il modulo WireGuard: il tunnel è `wireguard-go` (userspace) nel contenitore
  `wireguard` (`server/wireguard-synology`). Serve il pacchetto **Docker** di DSM (con `docker-compose` 1.28).
- `rdpm-sync` gira in un secondo contenitore **nella rete del contenitore `wireguard`**
  (`network_mode: "container:wireguard"`): ascolta anche su `10.66.0.1:8787` (per i PC già in VPN) e sulla porta 8787
  che il contenitore wireguard pubblica **solo su 127.0.0.1 del Synology**.
- Condivide con `wireguard` due cartelle: `../wireguard/config` (`wg0.conf` e `server.pub`) e `../wireguard/run`
  (il socket di `wireguard-go`, col quale il comando `wg set` aggiunge/toglie i PC **senza riavviare il tunnel**).
  Nell'immagine c'è solo `wireguard-tools` (il comando `wg`): niente moduli del kernel; il contenitore è root ma con
  `cap_drop: ALL`.
- Il server scrive in `wg0.conf` solo un blocco tra i marcatori `# --- rdpm-sync: inizio ... ---` / `fine blocco`:
  i peer aggiunti a mano con `aggiungi-pc.sh` restano dove sono e i loro indirizzi non vengono riassegnati.
  All'avvio il server riallinea `wg0.conf` e il tunnel al suo database (i peer di account eliminati spariscono).
- **I PC senza VPN** (la prima registrazione) non raggiungono `10.66.0.1`: arrivano da Internet su **HTTPS**,
  attraverso il proxy inverso di DSM che inoltra a `http://localhost:8787`. Il default del cliente è
  `https://syncnetpro.com:8443`.

## Messa in opera sul Synology

Prerequisiti: pacchetto Docker di DSM 7.1 attivo, contenitore `wireguard` già avviato con `prima-generazione.sh`
eseguito (esistono `wg0.conf` e `server.pub` in `/volume1/docker/wireguard/config`), SSH attivo, DNS
`syncnetpro.com` verso l'IP di casa.

1. Copia `server/wireguard-synology` in `/volume1/docker/wireguard` (il suo `docker-compose.yml` ora monta anche
   `./run`) e ricrea il contenitore, così monta la cartella del socket:
   `cd /volume1/docker/wireguard && mkdir -p run && sudo env PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin /usr/local/bin/docker-compose up -d --build`
2. Copia `server/rdpm-sync` (senza `bin/` e `obj/`) in `/volume1/docker/rdpm-sync`, **accanto** a `wireguard` (il
   compose usa `../wireguard/config` e `../wireguard/run`).
3. In `/volume1/docker/rdpm-sync`: `cp .env.example .env && chmod 600 .env` e compila `.env`:
   - `RDPM_SYNC_ADMIN_KEY`: frase casuale di almeno 16 caratteri (vuota = amministrazione HTTP spenta);
   - `RDPM_SYNC_REGISTRATION_CODE`: un codice che l'app chiede una volta alla registrazione (consigliato), **oppure**
     `RDPM_SYNC_OPEN_REGISTRATION=1` per la registrazione libera. Senza nessuno dei due la registrazione dall'app è
     spenta (resta il comando `crea-account` sul server);
   - facoltativi: `RDPM_VPN_ENDPOINT` (default `syncnetpro.com:51820`), `RDPM_VPN_DNS` (vuoto = nessun DNS).
4. `mkdir -p data` (il contenitore gira come root, non serve `chown`). Costruisci e avvia (il primo build scarica
   l'SDK .NET e `wireguard-tools`: diversi minuti, meglio in background):
   `sudo env PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin COMPOSE_HTTP_TIMEOUT=600 /usr/local/bin/docker-compose up -d --build`
5. Controlla:
   - `curl -fsS http://127.0.0.1:8787/health` risponde `{"status":"ok","version":"1.1.0"}`;
   - `sudo /usr/local/bin/docker exec rdpm-sync wg show wg0` mostra l'interfaccia (se non trova il socket, la
     cartella `run` non è condivisa: ricrea **prima** `wireguard`, poi `rdpm-sync`);
   - `sudo /usr/local/bin/docker logs rdpm-sync` non contiene errori «VPN: allineamento iniziale».
6. **HTTPS per i PC senza VPN**: DSM, *Pannello di controllo -> Portale di accesso -> Avanzate -> Proxy inverso ->
   Crea*: origine HTTPS, nome host `syncnetpro.com`, porta **8443**; destinazione HTTP `localhost`, porta **8787**.
   Certificato (Let's Encrypt) per `syncnetpro.com` in *Sicurezza -> Certificato*, assegnato a quella regola. Sul
   router inoltra **TCP 8443** verso il Synology (192.168.1.199) e apri la porta nel firewall DSM. Prova da fuori
   LAN: `curl https://syncnetpro.com:8443/health`. (Con un'altra porta cambia anche `SyncHttp.PublicServerUrl` nel
   client; nella finestra dell'account l'indirizzo si può comunque scrivere a mano.)
7. Prova completa: dall'app, *Strumenti -> Account e sincronizzazione -> Registrati*; a fine registrazione la VPN è
   accesa e `sudo /usr/local/bin/docker exec rdpm-sync wg show wg0` mostra il nuovo peer con un «latest handshake».

Se il contenitore `wireguard` viene ricreato, `rdpm-sync` perde rete e socket: in `/volume1/docker/rdpm-sync` lancia
`docker-compose up -d --force-recreate` (all'avvio i peer si riapplicano da `wg0.conf` e dal database). Dopo il
riavvio del NAS, se `rdpm-sync` è partito prima di `wireguard`, basta `docker restart rdpm-sync`.

Da salvare nelle copie di riserva (Hyper Backup): `./data/rdpm-sync.db` e `/volume1/docker/wireguard/config`
(chiavi del server e `wg0.conf`).

### Rete e sicurezza
- La porta 8787 è pubblicata **solo** su 127.0.0.1 del Synology: dall'esterno si entra solo dal proxy inverso HTTPS
  (8443). Mai aprire la 8787 sul router.
- Il traffico dei PC senza VPN è HTTPS: serve per il token e per potersi fidare della chiave del server WireGuard che
  si riceve. Dalla VPN vale anche `http://10.66.0.1:8787`.
- Dopo 5 accessi sbagliati (stesso indirizzo e stesso utente) il login si blocca per 15 minuti; la registrazione
  dall'app è limitata a 10 tentativi ogni 15 minuti per indirizzo.
- Il log non contiene password, token né documenti (solo nome utente e indirizzo di eventi come accesso
  riuscito/fallito, account creato, PC aggiunto/tolto dalla VPN).

## VPN automatica

Per un utente autenticato (Bearer; contratto completo in `docs/sync-server-api.md`):

| Chiamata | Effetto |
|---|---|
| `POST /v1/vpn/devices` `{name, publicKey}` | primo IP libero in `10.66.0.0/24` (da `.2`; `.1` è il server); peer salvato nel database, scritto in `wg0.conf` e applicato con `wg set`; risponde col profilo: `address` (`10.66.0.N/32`), `serverPublicKey`, `endpoint`, `allowedIPs`, `dns`, `persistentKeepalive`. Stessa chiave = stesso peer (200); stesso nome con chiave nuova = stesso indirizzo, chiave sostituita |
| `GET /v1/vpn/devices` | i PC di **questo** account |
| `DELETE /v1/vpn/devices/{nome}` | toglie il PC dall'account e dal tunnel |

Un utente vede e toglie solo i propri PC (quelli degli altri sono «inesistenti»). Eliminare l'account toglie anche i
suoi peer. Limite di 20 PC per account (`RDPM_VPN_MAX_DEVICES`). Senza `RDPM_VPN_CONF` o senza `server.pub` le
chiamate rispondono `503 vpn_disabled`. Se `wg` non risponde, l'aggiunta fallisce con `503 vpn_unavailable` e non
lascia nulla nel database.

## Creare un account

Dall'app: **Registrati** (se la registrazione è attiva, vedi `.env`). Oppure con il comando (la password si digita nel
terminale, non resta in nessun file):

```
sudo /usr/local/bin/docker exec -it rdpm-sync dotnet rdpm-sync.dll crea-account nicholas
```

Altri comandi: `lista-account`, `elimina-account <utente>` (toglie anche documento e token; i suoi PC spariscono dal
tunnel al prossimo riavvio del server, oppure elimina l'account via HTTP con la chiave di amministrazione, che li toglie
subito).

In alternativa, con la chiave di amministrazione via HTTP (serve il segreto derivato dalla password, cioè lo fa il
cliente: vedi il contratto, `POST /v1/admin/accounts`).

In RdpManager l'indirizzo di default è `https://syncnetpro.com:8443`; con la VPN accesa vale anche
`http://10.66.0.1:8787`. Più PC con lo stesso utente e la stessa password vedono gli stessi dati.

## Impostazioni (variabili d'ambiente)

| Variabile | Predefinito | Significato |
|---|---|---|
| `RDPM_SYNC_ADMIN_KEY` | vuota | chiave per `/v1/admin/*` (≥16 caratteri); vuota = amministrazione HTTP spenta |
| `RDPM_SYNC_DB` | `rdpm-sync.db` (nel container `/data/rdpm-sync.db`) | file del database |
| `RDPM_SYNC_LISTEN` | `127.0.0.1` (nel container `0.0.0.0`) | indirizzo di ascolto |
| `RDPM_SYNC_PORT` | `8787` | porta |
| `RDPM_SYNC_TOKEN_DAYS` | `30` | durata del login |
| `RDPM_SYNC_MAX_DOC_MB` | `8` | dimensione massima del documento |
| `RDPM_SYNC_OPEN_REGISTRATION` | vuota | `1` = chiunque raggiunga il server può registrarsi (`POST /v1/register`) |
| `RDPM_SYNC_REGISTRATION_CODE` | vuota | se impostato, la registrazione richiede questo codice (attiva anche senza `OPEN_REGISTRATION`) |
| `RDPM_VPN_CONF` | vuota (nel container `/vpn/wg0.conf`) | percorso di `wg0.conf`; vuota = VPN automatica spenta |
| `RDPM_VPN_SERVER_PUBLIC_KEY` | letta da `server.pub` accanto a `wg0.conf` | chiave pubblica del server WireGuard |
| `RDPM_VPN_ENDPOINT` | `syncnetpro.com:51820` | endpoint scritto nei profili |
| `RDPM_VPN_DNS` | vuota | DNS scritto nei profili |
| `RDPM_VPN_INTERFACE`, `RDPM_VPN_WG` | `wg0`, `wg` | interfaccia e comando `wg` |
| `RDPM_VPN_MAX_DEVICES` | `20` | PC per account |

## Senza Docker (prove)

```
dotnet run --project server/rdpm-sync
$env:RDPM_SYNC_ADMIN_KEY = "una-frase-casuale-lunga"   # PowerShell
dotnet run --project server/rdpm-sync -- crea-account prova
```
