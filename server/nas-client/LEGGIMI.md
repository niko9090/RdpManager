# marshmelloVPN sul NAS Synology

Con questo pacchetto un NAS Synology entra nella VPN marshmelloVPN **come un PC qualunque**: riceve un indirizzo
`10.66.0.N` e dagli altri PC della VPN raggiungi le cartelle condivise (`\\10.66.0.N`), DSM (`https://10.66.0.N:5001`)
e SSH, da casa o da fuori, senza Tailscale.

Funziona senza il modulo WireGuard del kernel (DSM non ce l'ha) e senza Docker: usa **wireguard-go** (WireGuard in
spazio utente) e il comando **wg**, già compilati per **aarch64** e **x86_64**.

| Requisito | Come verificarlo (via SSH) |
|---|---|
| DSM 7.x (provato in simulazione; DSM 7.1, 7.2 e 7.4 hanno ciò che serve) | `cat /etc.defaults/VERSION` |
| processore x86_64 o aarch64 | `uname -m` |
| `/dev/net/tun` (c'è su DSM 7; se manca lo script prova a caricare `tun.ko`) | `ls -l /dev/net/tun` |
| un utente amministratore con SSH | Pannello di controllo → Terminale e SNMP → Abilita servizio SSH |

I NAS con processore a 32 bit (armv7, per esempio alcuni modelli «j» vecchi) non sono supportati.

## Installazione, passo per passo

1. **Prendi il file di configurazione** `RdpManager.rdpmconfig`: è lo **stesso file** che usi per i PC
   (`pwsh scripts/new-client-config.ps1`, oppure sul server
   `docker exec rdpm-sync dotnet rdpm-sync.dll export-client-config --url https://<tuo-dominio>:8443`). Contiene
   l'indirizzo del server, il codice di registrazione, l'endpoint e la chiave pubblica del server.
2. **Copia sul NAS** questa cartella (`server/nas-client`, intera, con `bin/`) e il file `.rdpmconfig`, per esempio con
   **File Station** nella tua cartella home: ti ritrovi `/volume1/homes/TUOUTENTE/nas-client` e
   `/volume1/homes/TUOUTENTE/RdpManager.rdpmconfig`. (Se la cartella home non è attiva, va bene qualunque cartella
   condivisa, per esempio `/volume1/public`.)
3. **Collegati via SSH** dal PC (PowerShell o Terminale): `ssh TUOUTENTE@IP-DEL-NAS`
4. **Lancia l'installazione** (DSM chiede la password del tuo utente per `sudo`):

   ```
   cd /volume1/homes/TUOUTENTE/nas-client
   sudo sh installa-nas.sh --config ../RdpManager.rdpmconfig
   ```

5. Alla fine compare **«FATTO: il NAS è nella VPN con l'indirizzo 10.66.0.N»**. Annota l'indirizzo.
6. Da un PC con marshmelloVPN accesa: `ping 10.66.0.N`, poi in Esplora risorse `\\10.66.0.N`.
7. Se il **firewall di DSM** è acceso, aggiungi la regola descritta in «Firewall» qui sotto.
8. Cancella dal NAS il file `.rdpmconfig` (contiene il codice di registrazione; al NAS non serve più).

Il nome del NAS nella VPN è il suo **nome server** (Pannello di controllo → Rete); per sceglierne un altro aggiungi
`--nome NAS-Ufficio`. Due NAS con lo stesso nome server vanno installati con `--nome` diversi.

Senza file: `sudo sh installa-nas.sh --server https://<tuo-dominio>:8443 --codice CODICE`.
Altri parametri: `--server-interno URL`, `--utente U --password P` (un account esistente al posto di quello del NAS),
`--endpoint HOST:PORTA`, `--chiave-server CHIAVE`, `--rete CIDR`, `--cartella DIR`, `--senza-avvio-automatico`,
`--non-avviare`. `sh installa-nas.sh --help` li elenca.

## Cosa fa l'installazione

1. Controlla processore, `/dev/net/tun` e i programmi del pacchetto (`bin/SHA256SUMS`).
2. Copia tutto in **`/volume1/@marshmellovpn`** (cartella nascosta sul volume dati: non si vede da File Station e
   **sopravvive agli aggiornamenti di DSM**, che invece possono ripulire `/usr/local`). Se il NAS ha un'installazione
   precedente la aggiorna e tiene chiavi e indirizzo.
3. **Crea le chiavi WireGuard sul NAS** (`stato/privata.key`, permessi 600, cartella `stato` 700): la chiave privata non
   lascia mai il NAS. Al server va solo la pubblica.
4. **Si registra su rdpm-sync con lo stesso flusso del client Windows**: un account del dispositivo `pc-<nome>` con una
   password casuale (salvata in `stato/account.json`, 600), `POST /v1/register` con il codice, login, `POST /v1/vpn/devices`
   con nome e chiave pubblica. Il server assegna l'indirizzo, lo scrive in `wg0.conf` e lo applica al tunnel. Il profilo
   ricevuto (indirizzo, endpoint pubblico e LAN, chiave del server) va in `stato/profilo.env`. Rilanciare l'installazione
   rimanda la stessa chiave: stesso indirizzo.
5. Installa l'avvio automatico **`/usr/local/etc/rc.d/S99marshmellovpn.sh`** e accende il tunnel.

## Come funziona il tunnel

`marshmellovpn.sh` avvia un **supervisore** in background (un processo `sh`, nessun servizio di DSM toccato):

- `wireguard-go` crea l'interfaccia **`mvpn0`**; `wg setconf` carica chiave, server, `PersistentKeepalive = 25`;
  `ip` mette l'indirizzo `10.66.0.N/32` e **una sola rotta: `10.66.0.0/24` verso `mvpn0`**. La rotta predefinita, la LAN
  del NAS, il DNS e Tailscale restano come sono.
- **Endpoint**: se il NAS sta nella stessa /24 dell'endpoint LAN del server (per esempio `192.168.1.x` con server
  `192.168.1.10:51820`) usa quello LAN, altrimenti il nome pubblico. Se in 20 s non arriva nessun handshake prova
  l'altro (dalla LAN il nome pubblico spesso non risponde: il router non fa l'hairpin NAT in UDP).
- **Tunnel morto**: se l'ultimo handshake è più vecchio di **200 s** (o non ce n'è nessuno dopo **2 minuti**, senza
  endpoint LAN) il tunnel viene **ricreato**: porta locale nuova, nome del server risolto di nuovo, endpoint scelto di
  nuovo. È lo stesso comportamento del servizio Windows (`src/RdpManager/Vpn/VpnServiceLoop.cs`). Se `wireguard-go` si
  ferma, il supervisore lo riaccende; se l'accensione fallisce (rete non pronta, nome non risolto) riprova ogni 15 s.

## Comandi di tutti i giorni (via SSH)

```
sudo sh /volume1/@marshmellovpn/marshmellovpn.sh stato     # acceso? ultimo handshake? ultime righe del registro
sudo sh /volume1/@marshmellovpn/marshmellovpn.sh ferma
sudo sh /volume1/@marshmellovpn/marshmellovpn.sh avvia
sudo sh /volume1/@marshmellovpn/marshmellovpn.sh riavvia
sudo sh /volume1/@marshmellovpn/marshmellovpn.sh log       # ultime 50 righe
sudo /volume1/@marshmellovpn/bin/wg show mvpn0             # i dettagli di WireGuard
```

Il **registro** è `/volume1/@marshmellovpn/log/marshmellovpn.log` (una riga per evento, in italiano: accensione con
endpoint e porta, primo handshake, ricreazioni e perché; oltre 1 MB passa a `.log.1`). Se all'avvio il volume non
c'è, lo script di avvio lo scrive in `/var/log/marshmellovpn-avvio.log`.

## Avvio all'accensione del NAS

DSM 7, all'accensione, esegue **come root** gli script `*.sh` in `/usr/local/etc/rc.d/` con il parametro `start` (e
`stop` allo spegnimento): è lo stesso meccanismo che usa già `S99rdpm-avvio.sh` del server sul Synology (DSM 7.1). Lo
script del NAS non blocca l'avvio: aspetta in background che `/volume1` sia montato (fino a 10 minuti) e poi lancia
`marshmellovpn.sh avvia`.

Un aggiornamento importante di DSM può cancellare `/usr/local/etc/rc.d`. Per sicurezza aggiungi anche
un'**attività di avvio** (è innocua: se marshmelloVPN è già acceso non fa niente):

**Pannello di controllo → Utilità di pianificazione → Crea → Attività attivata → Script definito dall'utente**;
Generale: nome `marshmelloVPN`, utente **root**, evento **Avvio**; Impostazioni attività, Comando:

```
sh /volume1/@marshmellovpn/marshmellovpn.sh avvia
```

Dopo ogni aggiornamento di DSM basta comunque rilanciare l'installazione (tiene chiavi e indirizzo).

## Firewall di DSM

Il pacchetto **non tocca il firewall**. Il tunnel esce verso il server (UDP 51820) e le risposte rientrano come
connessione già stabilita: per il tunnel non serve aprire niente, né sul NAS né sul router di dove sta il NAS.

Se il firewall di DSM è **spento** (Pannello di controllo → Sicurezza → Firewall), non c'è niente da fare.
Se è **acceso**, i servizi del NAS (SMB, DSM, SSH) vanno permessi anche a chi arriva dalla VPN, cioè dalla rete
`10.66.0.0/24`, che per DSM è un'origine nuova:

1. Pannello di controllo → Sicurezza → Firewall → **Modifica regole** del profilo attivo (se il profilo ha regole per
   singola interfaccia, scegli «Tutte le interfacce», perché `mvpn0` non compare nell'elenco).
2. **Crea**: Porte → «Seleziona da un elenco di applicazioni integrate»: il file server Windows (SMB, 445 e 137–139),
   l'interfaccia di gestione di DSM (5000/5001), il terminale cifrato (SSH 22) — oppure le porte che usi.
   IP di origine → **Specifico** → Subnet `10.66.0.0`, maschera `255.255.255.0`. Azione **Consenti**.
3. Sposta la regola **sopra** l'eventuale «Nega tutto» e salva.

## Tailscale

Il pacchetto **non tocca Tailscale**: le due VPN convivono (Tailscale usa `100.64.0.0/10` e `tailscale0`, marshmelloVPN
`10.66.0.0/24` e `mvpn0`). Quando hai verificato che dai PC raggiungi il NAS su `10.66.0.N` (cartelle, DSM, SSH),
togli Tailscale da **Centro pacchetti → Tailscale → Disinstalla**. Attenzione: se oggi entri nel NAS via SSH
**attraverso Tailscale**, prima prova l'accesso via `ssh TUOUTENTE@10.66.0.N`.

## Disinstallare

```
sudo sh /volume1/@marshmellovpn/disinstalla-nas.sh
```

Ferma tunnel e supervisore, **toglie il NAS dalla VPN sul server** (l'indirizzo torna libero), cancella lo script di
avvio e la cartella. `--tieni-sul-server` non lo toglie dal server, `--tieni-chiavi` lascia `stato/` (reinstallando
riprende lo stesso indirizzo). L'eventuale attività nell'Utilità di pianificazione va tolta a mano.

## Problemi frequenti

| Messaggio | Cosa fare |
|---|---|
| `serve l'amministratore` | lancia con `sudo` |
| `il server non risponde` | controlla Internet sul NAS e l'indirizzo del server nel file (`curl https://<tuo-dominio>:8443/health`) |
| `il codice di registrazione non è accettato` | il file è vecchio: generane uno nuovo |
| `utente o password sbagliati` | l'account `pc-<nome>` esiste già (installazione precedente cancellata senza `stato/`): usa `--nome` diverso o elimina l'account sul server (`elimina-account pc-<nome>`) |
| `stato` dice «nessun handshake» | guarda il registro: «nome del server non risolto» = DNS del NAS; nessun contatto = porta UDP 51820 del server chiusa |
| dal PC non vedi le cartelle ma il ping va | firewall di DSM (vedi sopra) |

## Per chi sviluppa

### Perché uno script e non un pacchetto `.spk`

Su DSM 7 i pacchetti di terze parti **non firmati da Synology non girano come root**: il Centro pacchetti li fa girare
con un utente proprio, senza `CAP_NET_ADMIN`, e senza quel permesso non si crea un'interfaccia TUN né si aggiungono
indirizzi e rotte. Anche il pacchetto Tailscale per DSM 7 deve chiedere all'utente di lanciare un comando come root
(attività pianificata) per avere la TUN. Un `.spk` avrebbe quindi comunque bisogno di un passo da root via SSH o
dall'Utilità di pianificazione, più firma, Centro pacchetti e versioni DSM da seguire. Lo script fa tutto con **un solo
comando `sudo`**, si aggiorna rilanciandolo, si disinstalla con un comando ed è leggibile e provabile.

### Cosa c'è nella cartella

| File | A cosa serve |
|---|---|
| `installa-nas.sh` | installazione e registrazione (lancia `mvpn-helper registra`) |
| `marshmellovpn.sh` | il supervisore e i comandi `avvia`/`ferma`/`stato`/`log`; con `MVPN_SOLO_FUNZIONI=1` carica solo le funzioni (prove) |
| `avvio-dsm.sh` | modello di `/usr/local/etc/rc.d/S99marshmellovpn.sh` |
| `disinstalla-nas.sh` | disinstallazione (copiato anche nella cartella installata) |
| `bin/amd64`, `bin/arm64` | `wireguard-go`, `wg`, `mvpn-helper` statici; `bin/SHA256SUMS`, `bin/VERSIONI.txt` |
| `helper/` | sorgente Go di `mvpn-helper` (chiavi X25519, segreto PBKDF2-SHA256 600000 + HKDF come `ClientKeys.cs`, chiamate a rdpm-sync, lettura del `.rdpmconfig`); solo libreria standard più le radici dei certificati di `golang.org/x/crypto/x509roots/fallback`, usate se DSM non ne espone |
| `build/` | costruzione riproducibile dei binari (`versioni.env`, `costruisci-binari.sh`) |
| `test/` | prove (`esegui-test.sh`) |

L'helper è in Go (e non in Python o in shell) perché la registrazione richiede PBKDF2 a 600000 iterazioni, HKDF,
X25519 e HTTPS: Python non è garantito su tutti i DSM e `openssl` di DSM non ha PBKDF2 da riga di comando; un binario
statico come `wireguard-go` funziona uguale su ogni NAS.

### Binari: versioni, provenienza, checksum

Versioni in `build/versioni.env`, dettagli in `bin/VERSIONI.txt`, impronte in `bin/SHA256SUMS` (le controlla
l'installazione). Fonti: Go da **go.dev/dl** (checksum verificato), moduli Go da **proxy.golang.org** (verificati con
`go.sum` e sum.golang.org), **wireguard-go** e **wireguard-tools** da **git.zx2c4.com** al commit atteso (lo script si
ferma se il commit non coincide). `wireguard-go` e `mvpn-helper` sono compilati con `CGO_ENABLED=0 -trimpath
-buildid=` (statici, riproducibili); `wg` è compilato in Alpine con musl e `-static` (arm64 con l'emulazione QEMU di
Docker Desktop).

```
bash server/nas-client/build/costruisci-binari.sh             # ricostruisce bin/ (serve solo Docker)
bash server/nas-client/build/costruisci-binari.sh --verifica  # ricostruisce a parte e confronta con bin/SHA256SUMS
```

Licenze: wireguard-go MIT, wireguard-tools GPL-2.0 (sorgenti ai commit indicati).

### Prove

```
bash server/nas-client/test/esegui-test.sh               # tutto (serve Docker)
bash server/nas-client/test/esegui-test.sh --senza-e2e   # solo shellcheck e prove delle funzioni
```

- **shellcheck** su tutti gli script (POSIX `sh` per quelli che girano sul NAS);
- **prove delle funzioni** di `marshmellovpn.sh` con busybox ash e con bash (la `/bin/sh` di DSM): scelta dell'endpoint,
  file per `wg setconf`, ciclo del supervisore con orologio finto (stessi casi di `VpnServiceLoopTests.cs`);
- **prove di `mvpn-helper`** (`go test`, lanciate da `costruisci-binari.sh`): vettore del segreto del contratto, chiavi,
  lettura del `.rdpmconfig`, flusso di registrazione contro un server finto;
- **end-to-end** (`test/esegui-e2e.sh`, `test/e2e/`): in Docker un server WireGuard finto (gli stessi `wireguard-go` e
  `wg`), il **vero rdpm-sync** di questo repository nella sua rete come sul Synology, e due «NAS» Linux: uno nella LAN del
  server (deve scegliere l'endpoint LAN), uno «fuori casa» (endpoint pubblico). Il `.rdpmconfig` lo esporta rdpm-sync.
  Verifica installazione, permessi, registrazione, endpoint, handshake, ping, rotte, NAS↔NAS attraverso la VPN,
  ricreazione del tunnel con porta nuova bloccando l'UDP, alternanza degli endpoint, riavvio con lo script di DSM e
  disinstallazione (il peer sparisce dal server). Nessun server o NAS vero viene toccato.

### Cosa non si può provare qui

Le prove girano su Linux generico, non su DSM: vanno verificati sul NAS vero l'avvio da `/usr/local/etc/rc.d` dopo un
riavvio, il firewall di DSM con la regola per `10.66.0.0/24`, SMB e DSM raggiungibili su `mvpn0`, la convivenza con
Tailscale acceso, la risoluzione del nome e i certificati HTTPS con l'ambiente di DSM.
