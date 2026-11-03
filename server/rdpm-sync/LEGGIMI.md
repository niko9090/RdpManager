# rdpm-sync — server di sincronizzazione di RdpManager

Un piccolo server (ASP.NET Core, .NET 8, SQLite, nessun servizio esterno) con account e login. Chi entra con
**lo stesso account** vede le stesse connessioni, cartelle e profili di credenziali in RdpManager.

**Il server non vede mai niente in chiaro**: RdpManager cifra il documento (formato `.rdpm`) sul PC, con una
chiave ricavata dalla password dell'account, e manda al server solo il blocco cifrato. Anche la password non
arriva mai al server: arriva un segreto derivato da essa. Se si perde la password dell'account, i dati non si
recuperano (il server non può aiutare). Il contratto per chi scrive il cliente è in
`docs/sync-server-api.md`.

## Avvio sul Synology (Container Manager)

1. Copia la cartella `server/rdpm-sync` sul Synology (per esempio in `/volume1/docker/rdpm-sync`).
2. Nella cartella: copia `.env.example` in `.env` e compila `RDPM_SYNC_ADMIN_KEY` con una frase casuale lunga
   (almeno 16 caratteri). `RDPM_SYNC_BIND_IP` è l'indirizzo LAN del Synology (`192.168.1.199`).
3. Avvia: in Container Manager → *Progetto* → *Crea* → cartella `rdpm-sync` (usa `docker-compose.yml`), oppure
   via SSH: `sudo docker compose up -d --build`.
4. Controlla: `http://192.168.1.199:8787/health` deve rispondere `{"status":"ok",...}`.

Il database sta in `./data/rdpm-sync.db` (cartella accanto al `docker-compose.yml`): **è l'unica cosa da
salvare** nelle copie di riserva (Hyper Backup).

### Rete e sicurezza
- La porta è pubblicata **solo** sull'indirizzo LAN (`RDPM_SYNC_BIND_IP`). Non aprire la porta sul router e non
  metterla su DSM «Portale di accesso» pubblico: il server si raggiunge dalla LAN o dalla VPN WireGuard
  (10.66.0.0/24, con la LAN del Synology instradata).
- Il traffico è HTTP semplice perché passa dalla VPN (già cifrata); il documento è comunque cifrato dal cliente.
  Il token di accesso viaggia nell'intestazione: non usare il server su una rete non fidata senza HTTPS.
- Dopo 5 accessi sbagliati (stesso indirizzo e stesso utente) il login si blocca per 15 minuti.
- Il log non contiene password, token né documenti (solo nome utente e indirizzo di eventi come accesso
  riuscito/fallito, account creato).

## Creare il primo account

Con il comando (la password si digita nel terminale, non resta in nessun file):

```
sudo docker exec -it rdpm-sync dotnet rdpm-sync.dll crea-account nicholas
```

Altri comandi: `lista-account`, `elimina-account <utente>` (toglie anche documento e token).

In alternativa, con la chiave di amministrazione via HTTP (serve il segreto derivato dalla password, cioè lo
fa il cliente: vedi il contratto, `POST /v1/admin/accounts`).

Poi, in RdpManager, si indica l'indirizzo `http://192.168.1.199:8787`, il nome utente e la password.
Più PC con lo stesso utente e la stessa password vedono gli stessi dati.

## Impostazioni (variabili d'ambiente)

| Variabile | Predefinito | Significato |
|---|---|---|
| `RDPM_SYNC_ADMIN_KEY` | vuota | chiave per `/v1/admin/*` (≥16 caratteri); vuota = amministrazione HTTP spenta |
| `RDPM_SYNC_DB` | `rdpm-sync.db` (nel container `/data/rdpm-sync.db`) | file del database |
| `RDPM_SYNC_LISTEN` | `127.0.0.1` (nel container `0.0.0.0`) | indirizzo di ascolto |
| `RDPM_SYNC_PORT` | `8787` | porta |
| `RDPM_SYNC_TOKEN_DAYS` | `30` | durata del login |
| `RDPM_SYNC_MAX_DOC_MB` | `8` | dimensione massima del documento |

## Senza Docker (prove)

```
dotnet run --project server/rdpm-sync
$env:RDPM_SYNC_ADMIN_KEY = "una-frase-casuale-lunga"   # PowerShell
dotnet run --project server/rdpm-sync -- crea-account prova
```

## Limiti noti (versione 1)
- Niente cambio password: per cambiarla si crea un nuovo account (e si esporta/importa il `.rdpm`).
- Un solo documento per account; le sovrascritture concorrenti sono bloccate (409) e il cliente deve unire.
