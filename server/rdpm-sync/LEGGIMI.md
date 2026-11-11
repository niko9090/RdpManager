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
  attraverso il proxy Caddy (`caddy-rdpm`, porta 8443) che inoltra a `http://localhost:8787`. Nell'app si scrive
  `https://<tuo-dominio>:8443` (il dominio di `config/locale.json`).

## Messa in opera sul Synology (automatica)

Dal PC, con `config/locale.json` compilato (vedi «Configurazione locale» in fondo: alias SSH del NAS con `sudo` senza password, dominio con DNS verso l'IP di casa):

```powershell
pwsh server/deploy-synology.ps1
```

Lo script (ripetibile, si può rilanciare per aggiornare) fa tutto da solo:
- installa il pacchetto Docker di DSM se manca;
- copia `server/wireguard-synology` in `/volume1/docker/wireguard`, `server/rdpm-sync` (senza `bin/obj`) in
  `/volume1/docker/rdpm-sync` e `server/caddy-synology` in `/volume1/docker/caddy`; `config/`, `data/` e `.env`
  già presenti sul NAS non vengono toccati;
- se mancano, genera le chiavi del server WireGuard (`prima-generazione.sh`) e crea `.env` con una chiave admin
  casuale di 40 caratteri e un `RDPM_SYNC_REGISTRATION_CODE` casuale di 32 (un `.env` esistente non è mai
  sovrascritto: si aggiungono solo le chiavi vuote o mancanti);
- avvia `wireguard`, `rdpm-sync` e `caddy-rdpm` con `restart: unless-stopped`, e installa
  `/usr/local/etc/rc.d/S99rdpm-avvio.sh` (da `wireguard-synology/scripts/avvio-nas.sh`): al riavvio del NAS Docker
  riparte i contenitori prima che esista `/dev/net/tun`, `wireguard` esce subito e `rdpm-sync` con lui (il proxy
  risponde 502 e nessuno può accedere). Lo script aspetta Docker e `/dev/net/tun`, li riaccende e controlla
  `/health`; scrive in `/var/log/rdpm-avvio.log`. A mano: `sudo sh /usr/local/etc/rc.d/S99rdpm-avvio.sh adesso`;
- **HTTPS pubblico**: `caddy-rdpm` ascolta su **8443** (host) e inoltra a `127.0.0.1:8787`, con il certificato
  Let's Encrypt del tuo dominio che DSM già gestisce e rinnova (cartella in `/usr/syno/etc/certificate/_archive`,
  montata in sola lettura; Caddy lo rilegge ogni 12 ore). Le porte 80/443 restano a DSM, nessuna regola nel proxy
  inverso di DSM;
- apre sul router, via **UPnP**, TCP 8443 e UDP 51820 verso il Synology (se il router non supporta UPnP lo script
  avvisa: va inoltrata a mano);
- verifica `/health` (locale e via HTTPS) e `wg show wg0` dentro `rdpm-sync`, poi stampa l'ultima riga
  `REGISTRATION_CODE=...`: è il codice che l'app chiede una volta alla registrazione (si rilegge anche in
  `/volume1/docker/rdpm-sync/.env`). Non va nel repository.

Prova da fuori LAN: `curl https://<tuo-dominio>:8443/health`. Con un'altra porta cambia anche
`SyncHttp.PublicServerUrl` nel client (nella finestra dell'account l'indirizzo si può scrivere a mano).
Prova completa: dall'app, *Strumenti -> Account e sincronizzazione -> Registrati*; a fine registrazione la VPN è
accesa e `sudo /usr/local/bin/docker exec rdpm-sync wg show wg0` mostra il nuovo peer con un «latest handshake».

**File di configurazione per i client (`.rdpmconfig`)**: invece di far compilare campi, si consegna un file con i valori veri del
server (endpoint, chiave pubblica, codice di registrazione, DNS). Dal server:
`docker exec rdpm-sync dotnet rdpm-sync.dll export-client-config --url https://<tuo-dominio>:8443 > RdpManager.rdpmconfig`
(facoltativi: `--url-interno http://10.66.0.1:8787`, `--utente U --password P`, `--senza-chiave-al-server`). Dal PC di gestione,
senza entrare nel NAS: `pwsh scripts/new-client-config.ps1` (legge i valori da `config/locale.json` e via ssh). Il file contiene
il codice di registrazione: consegnalo in privato. Formato e uso nel README, sezione «File di configurazione unico».

Impostazioni facoltative in `.env`: `RDPM_VPN_ENDPOINT` (il deploy lo scrive dal dominio di `config/locale.json`), `RDPM_VPN_DNS`,
`RDPM_SYNC_OPEN_REGISTRATION=1` (registrazione libera, sconsigliata: chi si registra entra nella VPN).

Se il contenitore `wireguard` viene ricreato, `rdpm-sync` perde rete e socket: in `/volume1/docker/rdpm-sync` lancia
`docker-compose up -d --force-recreate` (all'avvio i peer si riapplicano da `wg0.conf` e dal database). Dopo il
riavvio del NAS ci pensa `S99rdpm-avvio.sh` (vedi sopra); se `/health` non risponde, guarda `/var/log/rdpm-avvio.log`.

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

In RdpManager l'indirizzo del server va scritto (nessun valore predefinito); con la VPN accesa vale anche
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
| `RDPM_VPN_ENDPOINT` | vuota (nessun default) | endpoint scritto nei profili |
| `RDPM_VPN_DNS` | vuota | DNS scritto nei profili |
| `RDPM_VPN_INTERFACE`, `RDPM_VPN_WG` | `wg0`, `wg` | interfaccia e comando `wg` |
| `RDPM_VPN_MAX_DEVICES` | `20` | PC per account |

## Senza Docker (prove)

```
dotnet run --project server/rdpm-sync
$env:RDPM_SYNC_ADMIN_KEY = "una-frase-casuale-lunga"   # PowerShell
dotnet run --project server/rdpm-sync -- crea-account prova
```

## Configurazione locale

Dominio, alias SSH del NAS e rete locale stanno in `config/locale.json` (radice del progetto), che non si pubblica: copia
`config/locale.example.json` e compilalo. `deploy-synology.ps1` lo legge e passa i valori allo script sul NAS; il `.env`
sul NAS (da `.env.example`) è escluso dal repository. Nel codice non c'è nessun indirizzo predefinito.
