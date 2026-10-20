# RdpManager

App desktop per Windows (C# .NET 8, WinForms) per gestire più desktop remoti dalla stessa finestra:
ogni sessione si apre in una **scheda** e si passa da un PC all'altro con un clic o con **Ctrl+Tab**.

Le sessioni usano il controllo ActiveX Desktop remoto di Windows (`mstscax.dll`, classe
`MsRdpClient*NotSafeForScripting`), ospitato da una sottoclasse di `AxHost` scritta nel codice
(nessun wrapper generato con aximp). Se il controllo incorporato non funziona, ogni connessione
si può aprire anche con il client classico `mstsc.exe`.

Repository: <https://github.com/niko9090/RdpManager>

## Installazione

1. Apri la pagina delle release: <https://github.com/niko9090/RdpManager/releases>.
2. Scarica `RdpManager-Setup-X.Y.Z.exe` dall'ultima release ed eseguilo.
   - L'installazione è **per utente** e non chiede diritti di amministratore: il programma va in
     `%LOCALAPPDATA%\Programs\RdpManager`, con il collegamento nel menu Start (icona sul desktop
     facoltativa).
   - Windows SmartScreen può avvisare che l'editore è sconosciuto (l'installer non è firmato):
     «Ulteriori informazioni» → «Esegui comunque».
3. In alternativa `RdpManager-X.Y.Z-win-x64.exe` è la versione **portatile**: un solo file da
   avviare, senza installazione.

Per installare una versione nuova basta eseguire il nuovo Setup: si installa sopra la precedente.
La disinstallazione (da «App installate» di Windows) rimuove solo il programma: **le connessioni
salvate in `%APPDATA%\RdpManager` restano**.

## Aggiornamenti

**Come avvisa l'app.** Dal menu **Aiuto → Controlla aggiornamenti** RdpManager legge l'ultima
release su GitHub e, se è più recente della versione in uso, propone di aprirne la pagina nel
browser, da cui si scarica il nuovo Setup. Non scarica né installa nulla da solo; se manca la rete
o GitHub non risponde lo dice con un messaggio, senza bloccarsi. Inoltre all'avvio fa lo stesso
controllo una volta, in background e con un timeout di 5 secondi: se trova una versione nuova lo
segnala nella barra di stato in basso (clic = apre la pagina della release), altrimenti tace.

## Pubblicare un aggiornamento

La strada principale è lo script **`scripts/release.ps1`** (serve un PC con .NET 8 SDK, Inno
Setup 6 e `gh` collegato all'account GitHub):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.1.0
```

Lo script, in ordine:
1. si ferma se ci sono modifiche non salvate, se non si è sul ramo `main`, se il ramo è indietro
   rispetto a GitHub o se il tag `v1.1.0` (o la release) esiste già;
2. scrive la versione in `<Version>` di `src/RdpManager/RdpManager.csproj` (l'unico punto in cui
   sta: app, installer e controllo aggiornamenti la leggono da lì);
3. compila ed esegue tutti i test;
4. pubblica l'exe unico e compila l'installer con `build-installer.ps1` → `dist\`;
5. fa il commit «Versione 1.1.0», crea il tag `v1.1.0` e fa il push di `main` e del tag;
6. crea la release su GitHub con `gh release create`, allegando `RdpManager-Setup-1.1.0.exe` e
   `RdpManager-1.1.0-win-x64.exe` (note personalizzabili con `-Notes "..."`).

A ogni push il workflow `.github/workflows/ci.yml` ricompila ed esegue i test del Core su GitHub
(scheda «Actions»); non pubblica nulla.

Solo installer, senza pubblicare:
`powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1` → cartella `dist\`.
Prova completa di installazione e disinstallazione silenziosa:
`powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1`.

## Requisiti

- Windows 10/11
- .NET SDK 8 (per compilare); l'eseguibile pubblicato in modalità *self-contained* non richiede .NET installato

## Compilare, provare, avviare

```powershell
dotnet build RdpManager.sln -c Release
dotnet test RdpManager.sln -c Release
dotnet run --project src/RdpManager
```

I test (99 + 1 facoltativo; nel CI solo quelli del Core, con `--filter "Category!=UI"`) comprendono:
- **Core**: salvataggio/ricaricamento JSON, cifratura DPAPI (nessuna password in chiaro nel JSON),
  lettura/generazione `.rdp`, cartelle (crea/rinomina/elimina), file temporaneo per mstsc;
- **integrazione interfaccia** (`UiIntegrationTests`): aprono la finestra principale vera con il
  controllo ActiveX RDP verso host irraggiungibili — scheda e stato con motivo di disconnessione,
  Riconnetti, più sessioni e Ctrl+Tab, tasti inoltrati al PC remoto, stacca/riaggancia senza
  ricreare il controllo, chiusura. Durano circa 25 secondi, richiedono una sessione Windows
  interattiva e usano un archivio temporaneo (non toccano `%APPDATA%\RdpManager`).

### Prova facoltativa con un server RDP reale

`LiveRdpServerTests` viene **saltata** se non si imposta la variabile `RDPMANAGER_LIVE_HOST`.
Usa un utente inventato (`rdpmanager-prova`), nessuna password, la richiesta interattiva di
credenziali disattivata (e verificata sul controllo) e un archivio temporaneo; scrive log e
screenshot in `RDPMANAGER_LIVE_OUT` (o in `%TEMP%\RdpManagerLive`).

```powershell
$env:RDPMANAGER_LIVE_HOST = "nome-del-pc"
dotnet test RdpManager.sln -c Release --filter "FullyQualifiedName~LiveRdp"
```

Agli avvisi la prova risponde sempre «No» (mai «Sì»). Gli avvisi stessi sono provati anche senza
rete da `CertificateWarningTests`, con un server RDP finto su 127.0.0.1 che usa un certificato
autofirmato creato in memoria (non installato in alcun archivio).

## Avviso sul certificato e richiesta credenziali

- **Come comportarsi:** se compare «Impossibile verificare l'identità del computer remoto»,
  controlla che il nome del computer sia quello atteso; premi **Sì** solo se ti fidi della rete e
  del PC (tipico dei PC di casa/ufficio senza dominio), altrimenti **No**. Non spuntare «Non
  visualizzare più…» se non vuoi che Windows memorizzi la scelta.
- **Limite noto (vero anche dopo la correzione):** l'avviso e la richiesta credenziali di Windows
  sono finestre **modali** del controllo RDP: finché non si risponde, la finestra che contiene la
  sessione (anche con le altre schede) resta disabilitata. Per questo, quando l'avviso compare,
  RdpManager ripristina la finestra se era minimizzata, seleziona la scheda interessata e la porta
  in primo piano (se Windows non lo consente, il pulsante sulla barra applicazioni lampeggia);
  la scheda mostra «In attesa di conferma: certificato non verificabile o credenziali richieste».
- Senza password salvata, il controllo chiede le credenziali con la finestra «Sicurezza di
  Windows». Con un server NLA quella finestra appartiene a un altro processo (CredentialUIBroker).
- Se il PC remoto non richiede l'autenticazione a livello di rete (NLA), anche senza password il
  controllo arriva alla schermata di accesso remota e la scheda risulta «Connessa» (osservato con
  un PC Windows della rete locale).

## Prova manuale con un PC remoto vero

Le prove automatiche non entrano mai davvero in un PC remoto (non usano credenziali vere). Queste
verifiche vanno fatte a mano, con un account che può accedere al PC remoto (es. `PC-REMOTO`).
Accanto a ogni passo è indicato cosa deve succedere; se succede altro, annota il passo, il testo
mostrato nella barra della scheda e, se c'è, uno screenshot.

**Prima di iniziare:** avvisa chi usa il PC remoto. Su Windows 10/11 (non Server) l'accesso remoto
con lo stesso utente chiude la sua sessione locale; con un altro utente la blocca.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | `dotnet run --project src/RdpManager -c Release` | Si apre RdpManager con l'elenco a sinistra. |
| 2 | Ctrl+N: nome, host (es. `PC-REMOTO`), utente, password; risoluzione «Adatta alla finestra»; OK | La connessione compare nell'elenco. |
| 3 | Apri `%APPDATA%\RdpManager\connections.json` (menu File → Apri cartella dei dati) | C'è `protectedPassword`, la password in chiaro **non** compare. |
| 4 | Doppio clic sulla connessione | Nuova scheda, pallino arancione «Connessione in corso…». |
| 5 | Se compare «Impossibile verificare l'identità del computer remoto» | La finestra di RdpManager è in primo piano sulla scheda giusta. Controlla il nome del PC, poi «Sì» (senza spuntare «Non visualizzare più…»). |
| 6 | Attendi | Pallino verde «Connessa», si vede il desktop remoto; nessuna richiesta di password (è salvata). |
| 7 | Ridimensiona o massimizza la finestra di RdpManager | Dopo circa 1 s il desktop remoto si adatta alla nuova dimensione (oppure viene scalato, se il PC remoto non lo supporta). |
| 8 | F11 (o «Schermo intero») | Sessione a schermo intero; si esce dalla barra in alto o con Ctrl+Alt+Pausa, tornando nella scheda. |
| 9 | Clic nella sessione, poi Ctrl+N o F5 | Agiscono sul PC remoto, non su RdpManager. |
| 10 | Apri una seconda connessione (anche verso lo stesso PC con un altro nome), poi Ctrl+Tab e Ctrl+Shift+Tab con il clic dentro la sessione | Si passa da una scheda all'altra; entrambe restano connesse. |
| 11 | «Stacca» (Ctrl+Shift+D) | La sessione passa in una finestra separata **senza disconnettersi** e si può usare. |
| 12 | Chiudi quella finestra (o «Riaggancia») | La sessione torna in una scheda, sempre connessa. |
| 13 | Dal PC remoto: Start → Disconnetti | Pallino rosso con il motivo; «Riconnetti» (F5 fuori dalla sessione) la riapre. |
| 14 | «Apri con mstsc» | Si apre il client classico di Windows con host e utente già compilati (la password potrebbe essere richiesta). |
| 15 | Modifica la connessione togliendo la password, poi connetti | Compare «Sicurezza di Windows – Immettere le credenziali» e RdpManager va in primo piano; dopo l'inserimento si entra. |
| 16 | «Chiudi» sulla scheda connessa | Chiede conferma, poi disconnette e chiude la scheda. |
| 17 | Esci con sessioni connesse | Chiede conferma e chiude tutto. |

Alla fine puoi eliminare la connessione di prova (Canc) o l'intero file `connections.json`.
Per una verifica rapida senza credenziali c'è anche la prova automatica facoltativa descritta sopra
(`RDPMANAGER_LIVE_HOST`).

## Cosa è stato provato e cosa no

- **Provato automaticamente** (99 test): modello, salvataggio JSON e cifratura DPAPI, file `.rdp`,
  cartelle, ricerca, finestra principale con il controllo ActiveX vero, più sessioni, Ctrl+Tab,
  tasti inoltrati al remoto, stacca/riaggancia mentre la connessione è in corso, chiusure, avviso
  sul certificato e richiesta credenziali (con un server RDP finto locale).
- **Provato con un server vero** (un PC Windows della rete locale, ottobre 2026, senza credenziali): raggiungibilità,
  avviso sul certificato, stato e motivo nella scheda.
- **Non provato** (passi 6-17 qui sopra): accesso con password, NLA con credenziali, adattamento
  della risoluzione, schermo intero, stacca/riaggancia e Ctrl+Tab con sessione connessa,
  «Apri con mstsc» verso un PC reale.

## Pubblicare un eseguibile unico

```powershell
dotnet publish src/RdpManager -c Release -r win-x64 --self-contained
```

Il risultato è un solo file `RdpManager.exe` in
`src\RdpManager\bin\Release\net8.0-windows\win-x64\publish\`, copiabile su qualunque PC Windows x64.
(Il progetto attiva `PublishSingleFile` automaticamente quando si indica un runtime con `-r`.)

## Uso

### Connessioni (elenco a sinistra)

- **Nuova** (Ctrl+N): nome, host (anche nella forma `host:porta`), porta (predefinita 3389), utente,
  dominio, password facoltativa, cartella, risoluzione (*adatta alla finestra* oppure fissa),
  condivisione appunti e unità locali.
- **Modifica** (Ctrl+E o F2), **Duplica** (Ctrl+D), **Elimina** (Canc).
- **Cartelle**: *Nuova cartella* (Ctrl+Shift+N) crea una cartella dentro quella selezionata; le
  sottocartelle si possono scrivere anche direttamente nel campo *Cartella* con `/`
  (es. `Clienti/Rossi`). Le connessioni si spostano trascinandole su una cartella. Eliminare una
  cartella non elimina le connessioni: passano alla cartella superiore.
- **Ricerca veloce** (Ctrl+F): filtra per nome, host, utente, dominio e cartella; più parole = tutte
  devono comparire. Esc svuota la ricerca, Invio/freccia giù passa all'elenco.
- **Importa file .rdp** (Ctrl+I, menu File): importa uno o più file `.rdp` esistenti nella cartella
  selezionata (indirizzo, porta, utente/dominio, risoluzione, appunti, unità; la password solo se
  il file l'aveva cifrata per lo stesso utente Windows).

### Sessioni (schede)

- Doppio clic o **Invio** su una connessione la apre in una nuova scheda (se è già aperta, la porta
  in primo piano). Si possono tenere aperte più sessioni insieme.
- L'icona colorata della scheda mostra lo stato: grigio = pronta, arancione = in connessione,
  verde = connessa, rosso = disconnessa (il motivo compare nella barra della sessione e nel tooltip
  della scheda). **Riconnetti** (F5) ricrea la connessione.
- Barra della sessione e menu *Sessione* (anche clic destro sulla scheda):
  - **Schermo intero** (F11); si esce dalla barra di connessione in alto o con Ctrl+Alt+Pausa;
  - **Stacca** in una finestra separata (Ctrl+Shift+D); *Riaggancia* o chiudere quella finestra
    la riporta in una scheda senza disconnetterla;
  - **Apri con mstsc**: genera un file `.rdp` temporaneo e avvia `mstsc.exe` (alternativa se il
    controllo incorporato non funziona); il file viene cancellato dopo 30 secondi;
  - **Chiudi scheda** (Ctrl+F4, clic con il tasto centrale sulla scheda).

### Scorciatoie da tastiera

| Tasti | Azione |
|---|---|
| Ctrl+Tab / Ctrl+Shift+Tab | scheda successiva / precedente (intercettati dall'app anche con il fuoco nella sessione; non a schermo intero) |
| Ctrl+N / Ctrl+Shift+N | nuova connessione / nuova cartella |
| Ctrl+E o F2, Ctrl+D, Canc | modifica, duplica, elimina |
| Ctrl+F | ricerca |
| Ctrl+I | importa file .rdp |
| Invio | connetti la connessione selezionata |
| F5 / F11 | riconnetti / schermo intero |
| Ctrl+Shift+D | stacca in una finestra |
| Ctrl+F4 | chiudi scheda |

Quando il fuoco è dentro una sessione remota, tutte le combinazioni tranne Ctrl+Tab e
Ctrl+Shift+Tab vengono inviate al PC remoto (così Ctrl+N, F5 ecc. funzionano nelle app remote).

## Dove sono salvati i dati

- Connessioni e cartelle: `%APPDATA%\RdpManager\connections.json`
  (menu *File → Apri cartella dei dati*).
- Le password **non sono mai scritte in chiaro**: nel JSON c'è solo `protectedPassword`, cifrata
  con DPAPI (`ProtectedData`, ambito *CurrentUser*). Si può decifrare solo con lo stesso utente
  Windows sullo stesso PC; copiando il file su un altro PC le password andranno reinserite.
- I file `.rdp` temporanei di *Apri con mstsc* stanno in `%TEMP%\RdpManager` e contengono la
  password solo in forma cifrata DPAPI (`password 51:b:`); le versioni recenti di mstsc possono
  comunque chiederla di nuovo.
- L'app non modifica il registro di Windows e non salva credenziali di sistema (niente `cmdkey`).

## Struttura

```
RdpManager.sln
src/RdpManager/
  Core/   modello, salvataggio JSON, cifratura DPAPI, lettura/scrittura .rdp (senza interfaccia, testabile)
  Rdp/    hosting ActiveX (RdpClientHost : AxHost), dichiarazioni COM, avvio di mstsc
  UI/     finestra principale, schede delle sessioni, finestre di dialogo
tests/RdpManager.Tests/   test xUnit (Core; categoria UI = prove con finestre e controllo RDP)
installer/RdpManager.iss  script Inno Setup dell'installer
build-installer.ps1       pubblicazione + installer → dist\
verifica/                 script dei criteri di verifica e prova dell'installer
.github/workflows/ci.yml  build e test del Core su GitHub a ogni push
scripts/release.ps1       pubblicazione di una nuova versione (vedi «Pubblicare un aggiornamento»)
```
