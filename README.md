# RdpManager

App desktop per Windows (C# .NET 8, WinForms) per gestire più desktop remoti dalla stessa finestra:
ogni sessione si apre in una **scheda** e si passa da un PC all'altro con un clic o con **Ctrl+Tab**.
Dalla 1.1: **schermo intero dell'app** con una barra di passaggio fra le sessioni che compare dal
bordo in alto, scorciatoie che funzionano anche dentro la sessione remota (**Ctrl+Alt+Destra/Sinistra**,
Ctrl+Alt+1…9, Ctrl+Alt+Invio), **barra laterale** fissa o a comparsa con preferiti, recenti e stato
dei PC, scelta del **monitor** e uso di tutti i monitor, **esportazione/importazione** cifrata in `.rdpm`,
Wake-on-LAN, riconnessione automatica e altro (vedi «Uso»).

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
| 8 | Ctrl+Alt+Invio o F11 (o «Schermo intero») | RdpManager a schermo intero con la sessione; si esce con Ctrl+Alt+Invio o con «Esci» nella barra che compare dal bordo in alto (vedi «Prova manuale 1.1»). |
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

## Prova manuale 1.1

Da fare dopo la prova qui sopra, con **almeno due** PC remoti connessi (anche lo stesso PC con due
connessioni diverse) e, se possibile, due monitor. Stesse avvertenze: avvisa chi usa i PC remoti.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | Avvia la 1.1 su un PC che aveva la 1.0.0 | Tutte le connessioni, cartelle e password di prima ci sono ancora. |
| 2 | Apri due sessioni e attendi «Connessa» su entrambe | Nell'elenco le due connessioni hanno il pallino verde pieno e il nome in grassetto; in *Recenti* compaiono loro. |
| 3 | Clic dentro una sessione, poi **Ctrl+Alt+Destra** e **Ctrl+Alt+Sinistra** | Si passa all'altra sessione connessa; il PC remoto non riceve la freccia. |
| 4 | Clic dentro una sessione, **Ctrl+Alt+Invio** | RdpManager copre tutto il monitor senza bordi, schede e barre spariscono; per un attimo compare la barra in alto. Il desktop remoto si adatta alla nuova dimensione. |
| 5 | Mouse sul bordo in alto dello schermo | Compare la barra di passaggio con le sessioni (le connesse numerate 1, 2…). Clic su una: si passa lì. Allontanando il mouse la barra sparisce. |
| 6 | **Ctrl+Alt+1** e **Ctrl+Alt+2** con il clic dentro la sessione | Si va alla prima e alla seconda sessione connessa. |
| 7 | **Ctrl+Alt+B**, poi di nuovo | La barra laterale compare sopra la sessione / sparisce, anche a schermo intero. |
| 8 | Barra laterale: «A comparsa», poi mouse sul bordo sinistro | La barra si nasconde; avvicinando il mouse al bordo sinistro riappare sopra la sessione, allontanandolo si richiude. «Fissa» la rimette accanto alle sessioni. |
| 9 | Con due monitor: *Monitor ›* nella barra in alto | Lo schermo intero passa all'altro monitor; con scala diversa (es. 100 % e 150 %) il desktop remoto si adatta senza riconnettersi. Riaprendo l'app, la finestra si apre su quel monitor. |
| 10 | Esci dallo schermo intero (Ctrl+Alt+Invio o «Esci») | La finestra torna com'era, con menu, schede e barre. |
| 11 | Modifica una connessione: *Usa tutti i monitor*, poi schermo intero su quella sessione | Il desktop remoto si estende su tutti i monitor. |
| 12 | «Stacca», poi «Monitor ›» nella finestra staccata | La finestra passa sull'altro monitor e la sessione resta connessa. |
| 13 | Nella sessione apri il Blocco note; copia un testo sul PC locale; *Invia → Incolla appunti come digitazione* | Il testo viene digitato nel Blocco note remoto, a capo compresi. |
| 14 | *Invia → Ctrl+Alt+Canc* | Sul PC remoto compare la schermata di sicurezza (Blocca, Cambia utente…). Esc per tornare. |
| 15 | *Invia → Foto della sessione* | Messaggio in basso con il percorso; in `Immagini\RdpManager` c'è il PNG del desktop remoto, ed è anche negli appunti. |
| 16 | Modifica una connessione: audio «Sul PC remoto», stampanti e microfono attivi; riconnetti | L'audio del PC remoto non si sente in locale; sul PC remoto compaiono le stampanti locali (reindirizzate) e il microfono. |
| 17 | Modifica una connessione: *Riconnetti da sola, al massimo 3 volte*; poi stacca il cavo di rete o riavvia il PC remoto | La scheda mostra «nuovo tentativo tra N s (1 di 3)», poi si riconnette appena il PC torna; dopo 3 tentativi falliti si ferma. |
| 18 | Indica il MAC di un PC spento con Wake-on-LAN attivo; clic destro → *Accendi (Wake-on-LAN)* | Messaggio «Accensione inviata»; quando il PC si accende, l'elenco lo mostra *Raggiungibile*. |
| 19 | Clic destro su una cartella → *Connetti tutta la cartella* | Si apre una scheda per ogni connessione della cartella e delle sottocartelle. |
| 20 | *File → Esporta connessioni (.rdpm)* con password del file; apri il file con il Blocco note | Le connessioni ci sono; le password **no** (solo testo cifrato). |
| 21 | Su un altro PC/utente: *File → Importa connessioni (.rdpm)*, password sbagliata, poi giusta, *Unisci* | Con la password sbagliata non importa nulla e la richiede; con quella giusta aggiunge le connessioni con le password; reimportando lo stesso file non crea doppioni. |
| 22 | *File → Impostazioni*: cambia «Sessione successiva» in Ctrl+Alt+Pag giù; spunta *Riapri all'avvio*; chiudi l'app con due sessioni aperte e riaprila | La nuova scorciatoia funziona dentro la sessione; all'avvio le due sessioni si riaprono nello stesso ordine. |
| 23 | Passa a un'altra applicazione (Alt+Tab) e premi Ctrl+Alt+Destra lì | L'altra applicazione riceve i tasti normalmente: l'hook di RdpManager è attivo solo con RdpManager in primo piano. |

## Cosa è stato provato e cosa no

- **Provato automaticamente** (`dotnet test`): modello, salvataggio JSON e cifratura DPAPI, file `.rdp`,
  file `.rdpm` (andata e ritorno, password del file sbagliata, nessuna password in chiaro, unione senza
  doppioni), caricamento di un `connections.json` della 1.0.0, MAC e pacchetto Wake-on-LAN, scorciatoie e
  ciclo fra le sessioni, monitor; finestra principale con il controllo ActiveX vero: più sessioni,
  Ctrl+Tab, tasti inoltrati al remoto, stacca/riaggancia, avviso sul certificato e richiesta credenziali
  (server RDP finto locale); 1.1: schermo intero dell'app (finestra senza bordi sul monitor, tutti i
  monitor per le connessioni che lo chiedono), barra di passaggio, barra laterale fissa e a comparsa
  anche a schermo intero, Preferiti/Recenti, connetti cartella, riconnessione automatica con limite,
  riapertura all'avvio, e **Ctrl+Alt+Destra digitato davvero** (SendInput) con il fuoco nella finestra
  interna del controllo RDP. Lo stato «connessa» in queste prove è simulato.
- **Provato con clic e tasti veri** sull'app compilata (ottobre 2026, un monitor, connessioni finte):
  doppio clic sull'elenco, Ctrl+Alt+Invio dentro la sessione, barra in alto dal bordo e clic su una
  sessione, barra laterale a comparsa dal bordo sinistro a schermo intero, impostazioni, modifica.
- **Provato con un server vero** (un PC Windows della rete locale, ottobre 2026, senza credenziali):
  raggiungibilità, avviso sul certificato, stato e motivo nella scheda.
- **Non provato** (passi 6-17 della prima prova e tutta la prova 1.1): accesso con password, NLA con
  credenziali, adattamento della risoluzione e del DPI con sessione connessa, due monitor veri,
  Ctrl+Alt+Canc, incolla come digitazione, foto, audio/stampanti/microfono, Wake-on-LAN verso un PC
  vero, «Apri con mstsc» verso un PC reale.

## Pubblicare un eseguibile unico

```powershell
dotnet publish src/RdpManager -c Release -r win-x64 --self-contained
```

Il risultato è un solo file `RdpManager.exe` in
`src\RdpManager\bin\Release\net8.0-windows\win-x64\publish\`, copiabile su qualunque PC Windows x64.
(Il progetto attiva `PublishSingleFile` automaticamente quando si indica un runtime con `-r`.)

## Uso

### Barra laterale (connessioni)

- **Fissa o a comparsa**: il pulsante *Sblocca/Fissa* in alto a destra nella barra (o *Visualizza →
  Barra laterale fissa*). A comparsa la barra si nasconde e riappare portando il mouse sul bordo
  sinistro della finestra, sopra le sessioni, anche a schermo intero. **Ctrl+Alt+B** la mostra o la
  nasconde in entrambi i modi.
- In cima: **Preferiti** (★, clic destro → *Preferita* o Ctrl+Shift+F) e **Recenti** (ultime 8
  connessioni riuscite). Poi le cartelle.
- **Stato** di ogni connessione, nel pallino sull'icona e nel suggerimento: *Connessa* (verde pieno),
  *In connessione* (arancione), *Disconnessa* (rosso), *Raggiungibile* (anello verde: la porta RDP
  risponde), *Non raggiungibile* (anello grigio). La raggiungibilità si controlla all'avvio e ogni
  minuto (TCP sulla porta, 2 s di attesa); *Verifica raggiungibilità* la rifà subito.
- **Colore ed etichetta** (nella modifica): il colore tinge l'icona e la barra di passaggio,
  l'etichetta breve (es. `PROD`) compare accanto al nome nell'elenco, nelle schede e nella barra.
- **Nuova** (Ctrl+N): nome, host (anche nella forma `host:porta`), porta (predefinita 3389), utente,
  dominio, password facoltativa, cartella, risoluzione (*adatta alla finestra* oppure fissa),
  appunti, unità locali, stampanti, microfono, audio (su questo PC / sul PC remoto / nessuno),
  *Usa tutti i monitor*, riconnessione automatica con il numero massimo di tentativi, preferita,
  etichetta, colore, indirizzo MAC per il Wake-on-LAN. I «?» spiegano le opzioni meno ovvie.
- **Modifica** (Ctrl+E o F2), **Duplica** (Ctrl+D), **Elimina** (Canc).
- **Cartelle**: *Nuova cartella* (Ctrl+Shift+N) crea una cartella dentro quella selezionata; le
  sottocartelle si possono scrivere anche direttamente nel campo *Cartella* con `/`
  (es. `Clienti/Rossi`). Le connessioni si spostano trascinandole su una cartella. Eliminare una
  cartella non elimina le connessioni: passano alla cartella superiore.
  **Connetti tutta la cartella** (clic destro sulla cartella) apre una scheda per ogni connessione
  della cartella e delle sottocartelle.
- **Accendi (Wake-on-LAN)** (clic destro o menu *Connessioni*): invia il «magic packet» in broadcast
  all'indirizzo MAC della connessione, poi per due minuti controlla quando il PC risponde. Il PC
  deve avere il Wake-on-LAN attivo ed essere nella stessa rete locale.
- **Ricerca veloce** (Ctrl+F): filtra per nome, host, utente, dominio, cartella ed etichetta; più
  parole = tutte devono comparire. Esc svuota la ricerca, Invio/freccia giù passa all'elenco.
- **Importa file .rdp** (Ctrl+I, menu File): importa uno o più file `.rdp` esistenti nella cartella
  selezionata (indirizzo, porta, utente/dominio, risoluzione, appunti, unità, audio, stampanti,
  microfono, multimonitor; la password solo se il file l'aveva cifrata per lo stesso utente Windows).

### Esportazione e importazione (.rdpm)

- **File → Esporta connessioni (.rdpm)**: scrive tutte le connessioni e le cartelle in un file.
  Si sceglie fra *senza password* (le password non escono dal PC) e *con le password, cifrate con
  una password del file* (almeno 8 caratteri; AES-256-GCM con chiave da PBKDF2-SHA256). Nel file
  non c'è mai una password in chiaro.
- **File → Importa connessioni (.rdpm)**: se il file contiene password chiede la password del file
  (se è sbagliata non importa nulla e la richiede). Poi si sceglie:
  - **Unisci**: aggiunge solo le connessioni che mancano; un doppione (stesso host, porta, utente e
    dominio, oppure stesso identificativo) non viene duplicato, al massimo riceve la password se non
    l'aveva;
  - **Sostituisci**: l'elenco attuale viene cancellato e sostituito da quello del file (chiede conferma).
- Serve per spostare l'elenco su un altro PC o un altro utente Windows: il `connections.json` non
  basta, perché le sue password DPAPI valgono solo per l'utente che le ha salvate.

### Sessioni (schede)

- Doppio clic o **Invio** su una connessione la apre in una nuova scheda (se è già aperta, la porta
  in primo piano). Si possono tenere aperte più sessioni insieme.
- L'icona colorata della scheda mostra lo stato: grigio = pronta, arancione = in connessione,
  verde = connessa, rosso = disconnessa (il motivo compare nella barra della sessione e nel tooltip
  della scheda). **Riconnetti** (F5) ricrea la connessione.
- **Riconnessione automatica** (opzione della connessione): dopo una disconnessione non voluta l'app
  ritenta da sola dopo 2, 4, 8, 16, poi 30 secondi, fino al numero di tentativi scelto; non ritenta
  se la sessione l'hai chiusa tu o se credenziali o certificato sono stati rifiutati.
- Barra della sessione e menu *Sessione* (anche clic destro sulla scheda):
  - **Schermo intero** (Ctrl+Alt+Invio o F11): vedi sotto;
  - **Stacca** in una finestra separata (Ctrl+Shift+D); *Riaggancia* o chiudere quella finestra
    la riporta in una scheda senza disconnetterla. La finestra staccata si apre sul monitor della
    finestra principale; **Monitor ›** la sposta sul monitor successivo, adattando la dimensione
    alla scala (DPI) di quel monitor. Lì «Schermo intero» usa lo schermo intero del controllo RDP
    (si esce dalla sua barra in alto o con Ctrl+Alt+Pausa);
  - **Invia** → *Incolla appunti come digitazione*: digita il testo degli appunti nella sessione
    come dalla tastiera (utile dove gli appunti non passano, es. la schermata di accesso);
    *Ctrl+Alt+Canc*: lo invia al PC remoto (il tasto vero resta al PC locale);
    *Foto della sessione*: salva un PNG in `Immagini\RdpManager` e lo copia negli appunti;
  - **Apri con mstsc**: genera un file `.rdp` temporaneo e avvia `mstsc.exe` (alternativa se il
    controllo incorporato non funziona); il file viene cancellato dopo 30 secondi;
  - **Chiudi scheda** (Ctrl+F4, clic con il tasto centrale sulla scheda).
- **Riapri all'avvio** (File → Impostazioni): chiudendo l'app si ricordano le sessioni aperte e al
  prossimo avvio si riaprono, nello stesso ordine.

### Schermo intero dell'app e più monitor

- **Ctrl+Alt+Invio** (o F11, o *Visualizza → Schermo intero*): la finestra di RdpManager diventa
  senza bordi e copre tutto il monitor scelto, con tutte le schede e le sessioni dentro; le
  intestazioni delle schede e le barre spariscono, la sessione corrente occupa lo schermo.
- **Barra di passaggio**: porta il mouse sul bordo in alto dello schermo. Elenca le sessioni aperte
  con il colore e lo stato; le **connesse** hanno il numero della scorciatoia (Ctrl+Alt+1, 2, …).
  Clic su una sessione per passarci. A destra: *Ctrl+Alt+Canc*, *Foto*, *Barra laterale*,
  *Monitor ›* (con più monitor) ed *Esci*. Compare per un attimo anche dopo ogni cambio di sessione.
- **Ctrl+Alt+Destra / Ctrl+Alt+Sinistra** passano alla sessione connessa successiva / precedente
  (quelle non connesse si saltano), **Ctrl+Alt+1…9** alla prima, seconda… sessione connessa.
  Funzionano anche con il clic dentro la sessione remota.
- **Monitor**: *Visualizza → Monitor per lo schermo intero* (o File → Impostazioni) sceglie il
  monitor; viene ricordato e all'avvio la finestra si apre lì. Se il monitor non è più collegato si
  usa il principale. *Sposta la finestra sul monitor successivo* (o *Monitor ›* nella barra) cambia
  monitor anche a schermo intero.
- **Tutti i monitor**: una connessione con *Usa tutti i monitor* a schermo intero si estende su
  tutti i monitor come un unico desktop remoto; con *Apri con mstsc* usa la modalità multimonitor di
  Windows (`use multimon`).
- **Dimensione e DPI**: con «Adatta alla finestra» il desktop remoto segue la dimensione della scheda
  e la scala del monitor (100 %, 125 %, 150 %…), anche spostando la finestra su un monitor diverso,
  senza riconnettere, se il PC remoto lo permette (Windows 8.1/Server 2012 R2 e successivi);
  altrimenti l'immagine viene scalata.

### Scorciatoie da tastiera

Queste valgono **anche con il fuoco dentro la sessione remota** e si cambiano in *File →
Impostazioni* (clic sulla casella, poi la combinazione; serve Ctrl o Alt; Canc la disattiva):

| Tasti (predefiniti) | Azione |
|---|---|
| Ctrl+Alt+Destra | sessione connessa successiva |
| Ctrl+Alt+Sinistra | sessione connessa precedente |
| Ctrl+Alt+1 … Ctrl+Alt+9 | prima … nona sessione connessa (come i numeri della barra di passaggio) |
| Ctrl+Alt+Invio | schermo intero sì/no |
| Ctrl+Alt+B | mostra/nascondi la barra laterale |

Funzionano grazie a un hook di tastiera (`WH_KEYBOARD_LL`) che l'app installa **solo mentre è in
primo piano** e toglie appena si passa a un'altra applicazione; i tasti che non sono scorciatoie
proseguono normalmente. *Aiuto → Scorciatoie da tastiera* mostra quelle in uso.

Nell'app:

| Tasti | Azione |
|---|---|
| Ctrl+Tab / Ctrl+Shift+Tab | scheda successiva / precedente (anche con il fuoco nella sessione) |
| Ctrl+N / Ctrl+Shift+N | nuova connessione / nuova cartella |
| Ctrl+E o F2, Ctrl+D, Canc | modifica, duplica, elimina |
| Ctrl+Shift+F | preferita sì/no |
| Ctrl+F | ricerca |
| Ctrl+I | importa file .rdp |
| Ctrl+, | impostazioni |
| Invio | connetti la connessione selezionata |
| F5 / F11 | riconnetti / schermo intero |
| Ctrl+Shift+D | stacca in una finestra |
| Ctrl+F4 | chiudi scheda |

Quando il fuoco è dentro una sessione remota, tutte le altre combinazioni vengono inviate al PC
remoto (così Ctrl+N, F5 ecc. funzionano nelle app remote).

## Dove sono salvati i dati

- Connessioni, cartelle e impostazioni (scorciatoie, monitor, barra laterale, sessioni da
  riaprire): `%APPDATA%\RdpManager\connections.json` (menu *File → Apri cartella dei dati*). Un
  file della 1.0.0 si carica senza perdite: i campi nuovi prendono i valori predefiniti.
  Per provare con un elenco a parte: `RdpManager.exe --dati C:\percorso\prova.json`.
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
  UI/     finestra principale (schede, schermo intero, barra laterale, hook di tastiera), dialoghi
tests/RdpManager.Tests/   test xUnit (Core; categoria UI = prove con finestre e controllo RDP)
installer/RdpManager.iss  script Inno Setup dell'installer
build-installer.ps1       pubblicazione + installer → dist\
verifica/                 script dei criteri di verifica e prova dell'installer
.github/workflows/ci.yml  build e test del Core su GitHub a ogni push
scripts/release.ps1       pubblicazione di una nuova versione (vedi «Pubblicare un aggiornamento»)
```
