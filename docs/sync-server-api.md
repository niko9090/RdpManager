# Server di sincronizzazione (rdpm-sync): contratto per il cliente

Versione del contratto: **v1**. Codice del server: `server/rdpm-sync/`. Il cliente (RdpManager) implementa
solo ciò che è scritto qui; il file `server/rdpm-sync/ClientKeys.cs` è l'implementazione di riferimento
della cifratura (in C#, riusabile così com'è) e i test del server ne fissano i risultati.

Idea: **stesso account = stesso documento**. Il documento è l'esportazione `.rdpm` di RdpManager
(connessioni, cartelle, profili di credenziali, testi rapidi, opzionalmente impostazioni), cifrata dal cliente.
Il server conserva un blocco opaco per account, con una versione per evitare sovrascritture.

## 1. Generalità

- Base URL configurabile dall'utente, es. `http://192.168.1.199:8787` (LAN o VPN WireGuard). Nessun prefisso.
- Tutte le risposte JSON sono UTF-8, nomi in camelCase. Il documento viaggia come `application/octet-stream`.
- Errori: stato HTTP appropriato e corpo
  `{"error":"<codice>","message":"<testo in italiano>","currentEtag":null|"\"v3\"","retryAfterSeconds":null|N}`.
  Il cliente decide in base a `error`, mai al testo.
- Autenticazione (tutti gli endpoint `/v1/*` tranne `login` e `admin`): `Authorization: Bearer <token>`.
  Token mancante, scaduto o revocato → `401 unauthorized` con `WWW-Authenticate: Bearer`: il cliente rifà il login.
- Dimensione massima del documento: 8 MB (configurabile; oltre → `413 too_large`).

## 2. Cifratura lato cliente

La password dell'account **non lascia mai il PC**. Dalla password si derivano due chiavi indipendenti: una per
accedere al server (il «segreto di accesso»), una per cifrare il documento (che il server non conosce).

Dati: `utente` = nome utente senza spazi ai bordi e in minuscolo (`ToLowerInvariant`), `password` = testo UTF-8.

1. `salt = SHA-256( UTF8("rdpm-sync/v1/salt/" + utente) )` — 32 byte, deterministico (serve per non dover
   chiedere il sale al server prima del login).
2. `master = PBKDF2-HMAC-SHA256( UTF8(password), salt, 600000 iterazioni, 32 byte )` — stessa KDF e stesso numero
   di iterazioni dell'esportazione `.rdpm` (`RdpmFile.DefaultIterations`).
3. `segretoAccesso = Base64( HKDF-SHA256( ikm = master, salt = vuoto, info = "rdpm-sync/v1/auth", 32 byte ) )`
   (44 caratteri). È ciò che si manda al server nel campo `password`.
4. `chiaveCifratura = HKDF-SHA256( ikm = master, salt = vuoto, info = "rdpm-sync/v1/enc", 32 byte )`.
5. Il server custodisce `segretoAccesso` solo come hash PBKDF2-SHA256 con sale casuale: chi rubasse il database
   non ha né la password né la chiave di cifratura.

In .NET: `Rfc2898DeriveBytes.Pbkdf2(...)` e `HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: ...)`;
vedi `ClientKeys.cs`. Conviene derivare **una volta** all'accesso (richiede circa mezzo secondo) e tenere in
memoria la `chiaveCifratura` per la sessione, senza mai scriverla su disco; sul disco resta solo il token
(protetto con DPAPI come le altre password).

### Vettori di prova (devono coincidere)
`utente = "mario"`, `password = "correct horse battery staple"`:

- `segretoAccesso` = `LYH1gkWICwAF8nBgnDp+hpjco7z+BEnEW50myOk2GcA=`
- `chiaveCifratura` (hex) = `8b1035aaa59eca1969dcc4ff5176b960a45eddcddb2fe37b42b4cb66269aee13`

### Formato del blob (il documento sul server)

```
byte 0        : versione del formato = 0x01
byte 1..12    : nonce casuale (12 byte), nuovo a ogni salvataggio
byte 13..N-17 : testo cifrato AES-256-GCM
ultimi 16 byte: tag GCM
```
- AAD (dati autenticati, non cifrati): `UTF8("rdpm-sync/v1/doc/" + utente)`: un blob non si può spostare su un altro account.
- Testo in chiaro: **il file `.rdpm` come lo produce `RdpmFile.Export(doc, filePassword: <password dell'account>, includeSettings: …)`**,
  in UTF-8. Le password delle connessioni e dei profili sono quindi protette due volte (cifrate nel `.rdpm`
  con la password dell'account, poi l'intero file cifrato nel blob), e il blob decifrato è un normale `.rdpm`
  apribile con *Importa*. Al ritorno: `Open` del blob, poi `RdpmFile.Import(testo, password)`.
- Versione del formato sconosciuta (byte 0 ≠ 1) o tag errato: il cliente non sovrascrive niente e avvisa
  («documento non leggibile: password diversa o dati danneggiati»).
- Il server accetta solo blob di almeno 29 byte; non può verificarne il contenuto.

## 3. Endpoint

### `GET /health`
Senza login. `200 {"status":"ok","version":"1.0.0"}`.

### `POST /v1/login`
Richiesta: `{"username":"mario","password":"<segretoAccesso>","deviceName":"PC-Ufficio"}` (`deviceName` facoltativo, max 64).

| Stato | Significato |
|---|---|
| 200 | `{"token":"rdpm_…","tokenType":"Bearer","expiresUtc":"2026-02-01T12:00:00+00:00","username":"mario"}` |
| 400 `invalid_request` | campi mancanti o `password` non di 16–256 caratteri |
| 401 `invalid_credentials` | utente inesistente o password sbagliata (stessa risposta) |
| 429 `too_many_attempts` | troppi tentativi sbagliati; intestazione `Retry-After` (secondi) e `retryAfterSeconds` |

Il token dura 30 giorni (configurabile), si può revocare, e il server ne conserva solo l'hash. Limite tentativi:
5 falliti in 15 minuti per la coppia indirizzo+utente (poi blocco anche con la password giusta), 30 per
indirizzo, 20 per utente; un accesso riuscito azzera il contatore della coppia. Il nome utente non distingue
maiuscole e minuscole.

### `GET /v1/session`
`200 {"username":"mario","expiresUtc":"…"}`: serve a sapere se il token vale ancora. `401` se no.

### `POST /v1/logout`
Revoca il token usato. `204`.

### `POST /v1/logout-all`
Revoca tutti i token dell'account (tutti i PC dovranno rifare il login). `200 {"revoked":N}`.

### `GET /v1/document`
| Stato | Significato |
|---|---|
| 200 | corpo = blob; intestazioni `ETag: "v3"` e `Last-Modified` |
| 304 | se `If-None-Match` è uguale all'ETag attuale (nessun corpo) |
| 404 `no_document` | l'account non ha ancora un documento |

### `PUT /v1/document`
Corpo = blob (`application/octet-stream`). **La condizione è obbligatoria**:
- primo salvataggio: `If-None-Match: *`
- aggiornamento: `If-Match: "v3"` con l'ETag **letto per ultimo** dal cliente (da `GET` o dal `PUT` precedente).

| Stato | Significato |
|---|---|
| 201 | creato; `ETag: "v1"`, corpo `{"etag":"\"v1\"","version":1}` |
| 200 | aggiornato; `ETag: "v4"`, corpo `{"etag":"\"v4\"","version":4}` |
| 409 `conflict` | la versione è cambiata (un altro PC ha salvato): nulla è stato scritto; `ETag` e `currentEtag` indicano la versione attuale (`null` se il documento non esiste) |
| 400 `invalid_etag` | `If-Match` non nel formato `"vN"`, o mescolato con `If-None-Match` |
| 400 `invalid_document` | corpo troppo corto (< 29 byte) |
| 413 `too_large` | oltre il limite |
| 428 `precondition_required` | manca `If-Match`/`If-None-Match` |

L'ETag è **opaco** per il cliente (oggi `"v<numero>"`): lo si rimanda tale e quale, senza interpretarlo né
confrontarlo oltre l'uguaglianza.

#### Comportamento del cliente consigliato
1. All'avvio e a intervalli: `GET` con `If-None-Match: <ultimo ETag>`; se `200`, decifrare, **unire** al documento
   locale (`RdpmFile.ApplyImport` in modalità unione, o la logica della sincronizzazione su cartella) e ricordare l'ETag.
2. Dopo una modifica locale: costruire il `.rdpm`, cifrare, `PUT` con `If-Match`. Se `409`: rifare `GET`,
   unire, ritentare (al massimo qualche volta, poi avvisare).
3. `401`: rifare il login con la password dell'account (se la si è tenuta in memoria) o chiederla.
4. Server non raggiungibile: lavorare in locale e ritentare; mai perdere modifiche locali.
5. Cancellazioni: l'unione di due `.rdpm` non sa cancellare; il cliente deve gestire le cancellazioni come
   fa già la sincronizzazione su cartella (o confrontando con l'ultimo stato sincronizzato).

## 4. Amministrazione

Gli endpoint `/v1/admin/*` richiedono l'intestazione `X-Admin-Key: <RDPM_SYNC_ADMIN_KEY>`. Se la variabile non è
impostata rispondono `503 admin_disabled`; chiave sbagliata `401 unauthorized` (dopo 10 errori dallo stesso
indirizzo `429`). Non servono al cliente di tutti i giorni.

- `POST /v1/admin/accounts` `{"username":"mario","password":"<segretoAccesso>"}` → `201 {"username":"mario"}`;
  `400 invalid_username` (3–64 caratteri tra lettere, cifre, `.` `_` `-` `@`); `400 invalid_password`; `409 account_exists`.
- `GET /v1/admin/accounts` → `200 [{"username","createdUtc","disabled","hasDocument"}]`.
- `DELETE /v1/admin/accounts/{username}` → `204`, o `404 not_found`. Toglie anche token e documento.

Il modo più semplice per creare un account resta il comando sul server (deriva il segreto da sé):
`docker exec -it rdpm-sync dotnet rdpm-sync.dll crea-account mario`.

## 4b. Registrazione dall'app e VPN (dalla 1.1)

### `POST /v1/register`
Come `POST /v1/admin/accounts`, ma senza chiave di amministrazione: `{"username","password":"<segretoAccesso>","registrationCode":"..."}`.
Attiva solo se il server ha `RDPM_SYNC_OPEN_REGISTRATION=1` o `RDPM_SYNC_REGISTRATION_CODE`; altrimenti `403 registration_disabled`.
Con il codice impostato, codice mancante o sbagliato = `403 invalid_registration_code`. Altri errori come per l'amministrazione
(`400 invalid_username`, `400 invalid_password`, `409 account_exists`); 10 tentativi per indirizzo ogni 15 minuti, poi `429`.

### VPN: `/v1/vpn/devices` (Bearer)
Il PC genera la coppia di chiavi WireGuard e manda **solo la chiave pubblica**. `503 vpn_disabled` se il server non gestisce WireGuard.
- `POST /v1/vpn/devices` `{"name":"Ufficio","publicKey":"<base64, 44 car.>"}` → `201` (nuovo) o `200` (già presente):
  `{"name","address":"10.66.0.N/32","serverPublicKey","endpoint":"syncnetpro.com:51820","allowedIPs":"10.66.0.0/24","dns":"","persistentKeepalive":25}`.
  `name`: 1–64 caratteri tra lettere, cifre, spazio, `.` `_` `-`. Idempotente: stessa chiave = stesso peer; stesso nome con chiave
  nuova = stesso indirizzo e chiave sostituita. Errori: `400 invalid_name`, `400 invalid_public_key`, `409 key_in_use` (chiave di
  un altro account), `409 too_many_devices`, `409 network_full`, `503 vpn_unavailable` (WireGuard non risponde: nulla è salvato).
- `GET /v1/vpn/devices` → `200 [{"name","address","publicKey","createdUtc"}]`, solo i PC dell'account.
- `DELETE /v1/vpn/devices/{name}` → `204`, o `404 not_found` (anche se il PC è di un altro account).

## 5. Cose volutamente non nella v1
- Cambio password (richiederebbe di ricifrare il documento e cambiare il segreto di accesso in un solo passo).
- Documenti multipli per account, cronologia delle versioni.
- HTTPS: il server parla HTTP semplice; l'HTTPS per i PC senza VPN lo dà il proxy inverso del Synology (il contratto non cambia).
