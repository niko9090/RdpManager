# RdpManager

App desktop per Windows (C# .NET 8, WinForms) per gestire più desktop remoti dalla stessa finestra:
ogni sessione si apre in una **scheda** e si passa da un PC all'altro con un clic o con **Ctrl+Tab**.
Dalla 1.1: **schermo intero dell'app**, scorciatoie che funzionano anche dentro la sessione remota (**Ctrl+Alt+Destra/Sinistra**,
Ctrl+Alt+1…9, Ctrl+Alt+Invio), **barra laterale** fissa o a comparsa con preferiti, recenti e stato
dei PC, scelta del **monitor** e uso di tutti i monitor, **esportazione/importazione** cifrata in `.rdpm`,
Wake-on-LAN, riconnessione automatica e altro (vedi «Uso»).
Dalla 1.2: **Panoramica** con le miniature delle sessioni connesse (**Ctrl+Alt+G**), **ricerca rapida**
(Ctrl+Alt+Spazio), **vista affiancata** di due sessioni, **profili di credenziali** condivisi,
**Remote Desktop Gateway** per connessione, **testi rapidi**, icona nell'**area di notifica** con una
scorciatoia globale, **tema scuro**, **cronologia** delle connessioni, **aggiornamento con un clic**
verificato con SHA256 e importazione dalla cronologia di mstsc.
Dalla 1.3: meno motivi di allarme per gli **antivirus** (niente hook di tastiera, installer a cartella,
metadati e manifest, vedi «Antivirus e firma»), **aggiornamento automatico con conferma**, messaggi di
errore di connessione con **causa probabile e soluzione**, **Impostazioni** con aggiornamenti e log,
riordino delle connessioni **trascinandole**, impostazioni dell'app nei `.rdpm` e **log** a rotazione.
Dalla 1.4: **spazi di lavoro** (connessioni e layout riaperti con un clic o una scorciatoia), **blocco
con PIN** (anche dopo un tempo di inattività), **profili di qualità** per connessione, sessione **tenuta
sveglia**, **selezione multipla** con azioni di gruppo, importazione da **CSV** con anteprima e riepilogo
delle scorciatoie con **F1**.
Dalla 1.5: a schermo intero **in alto non c'è niente**; le sessioni connesse stanno nella **barra a schermo
intero**, personalizzabile: a sinistra, a destra o in basso, fissa (la sessione non viene coperta), a
comparsa o nascosta, con larghezza, ritardo e sezioni scelte e ordinate (vedi «Schermo intero dell'app»).
Le novità di ogni versione sono in [CHANGELOG.md](CHANGELOG.md).

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
   - Windows SmartScreen o l'antivirus possono avvisare o bloccare l'installer (non è firmato):
     vedi «Antivirus e firma» qui sotto.
   - Dalla 1.3 l'installer contiene il programma **a cartella** (`RdpManager.exe` con le librerie
     .NET), non più un exe unico compresso.
3. In alternativa `RdpManager-X.Y.Z-win-x64.exe` è la versione **portatile**: un solo file da
   avviare, senza installazione (exe unico **non compresso**). Non si aggiorna da sola: quando c'è
   una versione nuova apre la pagina della release.

Per installare una versione nuova basta eseguire il nuovo Setup: si installa sopra la precedente.
La disinstallazione (da «App installate» di Windows) rimuove solo il programma: **le connessioni
salvate in `%APPDATA%\RdpManager` restano**.

## Antivirus e firma

RdpManager non è firmato, quindi SmartScreen e alcuni antivirus possono bloccarlo, soprattutto nei primi
giorni dopo una release. Il 21/10/2026 Microsoft Defender in locale (`MpCmdRun.exe -Scan -ScanType 3`) non
ha trovato minacce né nella 1.2.0 né nella 1.3.0: i blocchi vengono dalla reputazione e dall'analisi
euristica. Dalla 1.3 l'app evita i comportamenti che queste analisi guardano:

- **nessun hook di tastiera** (`WH_KEYBOARD_LL` tolto): le scorciatoie dentro la sessione usano
  `RegisterHotKey`, solo mentre RdpManager è in primo piano;
- installer con il programma **a cartella** invece di un exe unico autoestraente;
- **metadati** completi (azienda, prodotto, descrizione, copyright, versione) nell'exe e nell'installer;
- manifest con **`asInvoker`**: non chiede mai i diritti di amministratore;
- nessun offuscamento; l'aggiornamento chiede sempre conferma ed esegue solo installer verificati.

Il testo pronto per segnalare il falso positivo a Microsoft
(<https://www.microsoft.com/wdsi/filesubmission>) e agli altri antivirus, e le istruzioni per una
futura **firma del codice** (Azure Trusted Signing o SignPath, senza segreti nel repository), sono in
[docs/falsi-positivi.md](docs/falsi-positivi.md).

## Aggiornamenti

Si scelgono in *File → Impostazioni → Avvio e aggiornamenti*:

- **Automatici con conferma** (predefinito): all'avvio e poi ogni 24 ore RdpManager controlla l'ultima
  release su GitHub (in sottofondo, con un timeout). Se è più nuova scarica `RdpManager-Setup-X.Y.Z.exe` in
  `%LOCALAPPDATA%\RdpManager\updates` e ne calcola lo SHA256, che deve coincidere con la riga dello stesso
  file in **`SHA256SUMS.txt`** allegato alla release; i file non validi vengono cancellati. Solo dopo la
  verifica compare un **avviso non modale** con le novità della release e tre scelte:
  - **Aggiorna ora**: se ci sono sessioni aperte chiede conferma (verranno chiuse e poi riaperte), salva le
    sessioni aperte, chiude l'app e avvia l'installer in silenzio
    (`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1`); a installazione finita
    RdpManager si riapre con le stesse sessioni;
  - **Alla chiusura**: installa nello stesso modo quando chiudi l'app, senza riaprirla;
  - **Più tardi**: lo ripropone dopo 24 ore o al prossimo avvio.
- **Solo avviso**: segnala la versione nuova nella barra di stato; clic = stesso avviso.
- **Spenti**: nessun controllo automatico.

Dal menu **Aiuto → Controlla aggiornamenti** si controlla a mano in ogni modalità. Subito prima di
avviare l'installer lo SHA256 viene **ricontrollato**: se il file è cambiato non si avvia nulla. La
versione **portatile** non si aggiorna da sola: apre la pagina della release. Connessioni, impostazioni e
cronologia restano sempre.

## Pubblicare un aggiornamento

La strada principale è lo script **`scripts/release.ps1`** (serve un PC con .NET 8 SDK, Inno
Setup 6 e `gh` collegato all'account GitHub):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.2.0
```

Lo script, in ordine:
1. si ferma se ci sono modifiche non salvate, se non si è sul ramo `main`, se il ramo è indietro
   rispetto a GitHub o se il tag `v1.2.0` (o la release) esiste già;
2. scrive la versione in `<Version>` di `src/RdpManager/RdpManager.csproj` (l'unico punto in cui
   sta: app, installer e controllo aggiornamenti la leggono da lì);
3. compila ed esegue tutti i test;
4. pubblica a cartella e compila l'installer con `build-installer.ps1` → `dist\` (più l'exe portatile), insieme a
   `SHA256SUMS.txt` (SHA256 dei due file, formato di `sha256sum`), e controlla che corrisponda;
5. fa il commit «Versione 1.2.0» (se la versione nel csproj è cambiata), crea il tag `v1.2.0` e fa il
   push di `main` e del tag;
6. crea la release su GitHub con `gh release create`, allegando `RdpManager-Setup-1.2.0.exe`,
   `RdpManager-1.2.0-win-x64.exe` e `SHA256SUMS.txt` (note: la sezione della versione in `CHANGELOG.md` più le istruzioni di installazione; oppure `-Notes "..."`).

`SHA256SUMS.txt` è indispensabile: senza, l'aggiornamento con un clic delle versioni 1.2 e successive
rifiuta l'installer.

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

I test (371 + 2 facoltativi; nel CI solo quelli del Core, con `--filter "Category!=UI"`) comprendono:
- **Core**: salvataggio/ricaricamento JSON, cifratura DPAPI (nessuna password in chiaro nel JSON),
  lettura/generazione `.rdp`, cartelle (crea/rinomina/elimina), file temporaneo per mstsc; dalla 1.2
  profili di credenziali (risoluzione, cifratura, `.rdpm` senza password in chiaro, unione senza
  doppioni), gateway nei `.rdp`, ricerca approssimata, testi rapidi, cronologia, verifica SHA256
  dell'aggiornamento (file giusto accettato, file alterato rifiutato e cancellato) e caricamento di un
  `connections.json` della 1.0.0 e della 1.1.0 (fixture inventati in `tests/RdpManager.Tests/Fixtures`);
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

**Schermate dell'interfaccia** (facoltativa, saltata se la variabile manca): salva in una cartella le
immagini della finestra con il tema scuro, della Panoramica, della ricerca rapida, della vista affiancata,
delle impostazioni e del dialogo della connessione, con connessioni inventate.

```powershell
$env:RDPMANAGER_SCREENSHOTS = "C:\cartella\schermate"
dotnet test RdpManager.sln -c Release --filter "FullyQualifiedName~Screenshot"
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
| 8 | Ctrl+Alt+Invio o F11 (o «Schermo intero») | RdpManager a schermo intero con la sessione e la barra a schermo intero a sinistra; si esce con Ctrl+Alt+Invio o con «Schermo intero sì/no» fra i pulsanti rapidi della barra (vedi «Prova manuale 1.5»). |
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
| 4 | Clic dentro una sessione, **Ctrl+Alt+Invio** | RdpManager copre tutto il monitor senza bordi, schede e menu spariscono. Il desktop remoto si adatta alla nuova dimensione. (Dalla 1.5 la barra in alto non c'è più: vedi «Prova manuale 1.5».) |
| 5 | Barra a schermo intero, sezione «Sessioni connesse» | Le sessioni con le connesse numerate 1, 2…. Clic su una: si passa lì. |
| 6 | **Ctrl+Alt+1** e **Ctrl+Alt+2** con il clic dentro la sessione | Si va alla prima e alla seconda sessione connessa. |
| 7 | **Ctrl+Alt+B**, poi di nuovo | La barra laterale compare sopra la sessione / sparisce, anche a schermo intero. |
| 8 | Barra laterale: «A comparsa», poi mouse sul bordo sinistro | La barra si nasconde; avvicinando il mouse al bordo sinistro riappare sopra la sessione, allontanandolo si richiude. «Fissa» la rimette accanto alle sessioni. |
| 9 | Con due monitor: *Visualizza → Sposta la finestra sul monitor successivo* | Lo schermo intero passa all'altro monitor; con scala diversa (es. 100 % e 150 %) il desktop remoto si adatta senza riconnettersi. Riaprendo l'app, la finestra si apre su quel monitor. |
| 10 | Esci dallo schermo intero (Ctrl+Alt+Invio) | La finestra torna com'era, con menu, schede e barre. |
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

## Prova manuale 1.2

Da fare con **almeno due** PC remoti connessi (anche lo stesso PC con due connessioni diverse). Stesse
avvertenze delle prove precedenti.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | Avvia la 1.2 su un PC che aveva la 1.1.0 (o la 1.0.0) | Connessioni, cartelle, password, preferiti, colori, scorciatoie e impostazioni di prima ci sono ancora. |
| 2 | *Connessioni → Profili di credenziali → Nuovo*: nome, utente, dominio, password | Il profilo compare nell'elenco. |
| 3 | Modifica due connessioni: *Credenziali* = il profilo; connetti | Entrambe entrano con utente e password del profilo; il suggerimento nell'elenco dice «(profilo …)». |
| 4 | Cambia la password del profilo, poi riconnetti una delle due | Usa la password nuova senza toccare la connessione. |
| 5 | Con un Remote Desktop Gateway: *Passa da un Remote Desktop Gateway*, host del gateway, credenziali «le stesse» o un profilo; connetti | La sessione passa dal gateway (sul gateway la si vede tra le connessioni). |
| 6 | *Apri con mstsc* sulla stessa connessione; poi clic destro → *Esporta come file .rdp*, e *File → Importa file .rdp* con quel file | mstsc passa dal gateway; il `.rdp` contiene il gateway e la connessione importata ha «Passa da un Remote Desktop Gateway» con lo stesso host. |
| 7 | Con tre sessioni connesse: **Ctrl+Alt+G** (anche col clic dentro una sessione) | Si apre la Panoramica con le miniature delle sessioni connesse; in pochi secondi tutte mostrano il desktop remoto e si aggiornano. |
| 8 | Clic su una miniatura (oppure frecce + Invio, o il numero 1-9) | Si passa a quella sessione; Esc o di nuovo Ctrl+Alt+G chiude senza cambiare sessione. |
| 9 | **Ctrl+Alt+Spazio**, scrivi parte del nome con un errore (es. «magazzno») | La connessione giusta è in cima; Invio apre una sessione nuova o passa a quella già aperta. |
| 10 | **Ctrl+Alt+S** con due sessioni aperte | Le due sessioni sono affiancate; il divisore si sposta e viene ricordato. Ctrl+Alt+Destra passa dall'una all'altra; Ctrl+Alt+S (o andare a una terza sessione) torna alle schede, senza riconnettere. |
| 11 | *Sessione → Testi rapidi → Gestisci*: aggiungi `ipconfig /all` + a capo; poi in una sessione con il Prompt dei comandi scegli il testo | Il comando viene digitato ed eseguito nel PC remoto. |
| 12 | *File → Impostazioni → Tema: Scuro*, poi *Come Windows* | Finestra, elenco, menu e dialoghi diventano scuri; con «Come Windows» seguono la modalità app di Windows. |
| 13 | Riduci a icona RdpManager, passa a un'altra app e premi **Ctrl+Alt+Maiusc+R** | RdpManager torna in primo piano. |
| 14 | Clic destro sull'icona nell'area di notifica → *Tutte le connessioni* → una connessione | La finestra torna davanti e la sessione si apre. |
| 15 | Impostazioni: *Chiudendo la finestra resta nell'area di notifica*; chiudi con la X | La finestra sparisce, le sessioni restano connesse; clic sull'icona la riapre; *Esci* dal suo menu chiude davvero. |
| 16 | Connetti e disconnetti una sessione; *Connessioni → Cronologia* | Righe con inizio, fine, durata e motivo; filtro per connessione; «Svuota quelle mostrate» le toglie. |
| 17 | *File → Importa dalla cronologia di mstsc* | Elenco dei PC che mstsc ricorda per il tuo utente (quelli già presenti sono senza spunta); importati nella cartella «Importate da mstsc». |
| 18 | Esporta in `.rdpm` con password del file; importa su un altro PC | Profili e testi rapidi arrivano con le connessioni; le password del profilo solo con la password del file giusta. |
| 19 | Quando esce la versione successiva: *Aiuto → Controlla aggiornamenti → Aggiorna ora* | Scarica il Setup, dice «verificato (SHA256 …)», chiede conferma, si chiude e l'installer aggiorna l'app tenendo i dati. |

## Prova manuale 1.3

Da fare su un PC dove RdpManager 1.2.0 è installato, con almeno due PC remoti. Stesse avvertenze delle
prove precedenti.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | Scarica `RdpManager-Setup-1.3.0.exe` ed eseguilo sopra la 1.2.0 | Nessun blocco dell'antivirus (al più l'avviso di SmartScreen); connessioni, profili, testi rapidi e impostazioni di prima ci sono ancora. |
| 2 | Esplora file → `%LOCALAPPDATA%\Programs\RdpManager\RdpManager.exe` → Proprietà → Dettagli | Descrizione, prodotto, versione 1.3.0, copyright e azienda compilati; accanto c'è `RdpManager.dll` (installazione a cartella). |
| 3 | Clic dentro una sessione connessa, **Ctrl+Alt+Destra** | Si passa all'altra sessione anche senza hook di tastiera (la barra di stato non segnala conflitti). |
| 4 | Passa a un'altra app (es. Blocco note) e premi Ctrl+Alt+Destra | L'altra app riceve i tasti: le scorciatoie di RdpManager valgono solo con RdpManager in primo piano. |
| 5 | Connessione con host sbagliato (es. `pc-inesistente`), poi con password sbagliata | Al posto della sessione: motivo, «Causa probabile» e «Cosa fare» adatti (nome non trovato / password sbagliata). |
| 6 | Trascina una connessione sopra un'altra della stessa cartella, poi in un'altra cartella | Si mette prima di quella (o in fondo alla cartella); l'ordine resta dopo il riavvio. *Visualizza → Ordina per nome* torna all'ordine alfabetico. |
| 7 | *File → Impostazioni*: tre schede; in *Avvio e aggiornamenti* attiva il log dettagliato, poi *Apri la cartella dei log* | Si apre `%LOCALAPPDATA%\RdpManager\logs`; in `rdpmanager.log` ci sono avvio e sessioni (con `c-xxxxxxxx`), mai password. |
| 8 | *File → Esporta (.rdpm)* con *Includi le impostazioni*; su un altro PC importa con *Applica anche le impostazioni* | Arrivano anche tema, scorciatoie e modalità degli aggiornamenti; monitor e sessioni da riaprire restano quelli del PC. |
| 9 | Quando esce la versione successiva, con due sessioni aperte: attendi l'avviso (o *Aiuto → Controlla aggiornamenti*) | Avviso non modale con le novità e «Aggiorna ora», «Alla chiusura», «Più tardi»; in `%LOCALAPPDATA%\RdpManager\updates` c'è solo l'installer verificato. |
| 10 | «Aggiorna ora» | Chiede conferma dicendo che le 2 sessioni verranno chiuse e riaperte; l'app si chiude, si aggiorna da sola e si riapre con le stesse sessioni. |
| 11 | La volta dopo: «Alla chiusura», poi chiudi l'app | L'aggiornamento si installa alla chiusura e l'app non si riapre. |
| 12 | Con la versione portatile: *Aiuto → Controlla aggiornamenti* | Propone di aprire la pagina della release (non si aggiorna da sola). |

## Prova manuale 1.4

Da fare su un PC dove RdpManager 1.3.0 è installato, con almeno due PC remoti.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | Lascia che la 1.3.0 proponga l'aggiornamento e scegli «Aggiorna ora» | Si aggiorna in silenzio e si riapre; connessioni, profili, impostazioni e ordine dell'elenco ci sono ancora. |
| 2 | Apri tre sessioni, metti due affiancate (Ctrl+Alt+S); *Spazi di lavoro → Salva lo spazio attuale* «Mattina» | Lo spazio compare nel menu con il numero di connessioni. |
| 3 | Chiudi tutto; *Spazi di lavoro → Mattina* | Si riaprono le tre sessioni nello stesso ordine, con le due affiancate (e schermo intero/monitor se c'erano). |
| 4 | *Spazi di lavoro → Gestisci → Scorciatoia…* `Ctrl+Alt+Maiusc+1`; chiudi tutto e premila (anche col clic dentro una sessione) | Lo spazio si riapre. |
| 5 | *File → Impostazioni → Avvio, blocco e aggiornamenti → Imposta PIN…*; poi **Ctrl+Alt+L** | La finestra sparisce e compare la schermata del PIN a tutto schermo; il menu dell'icona di notifica porta solo alla schermata. |
| 6 | PIN sbagliato più volte, poi quello giusto | «PIN sbagliato»; dopo 5 errori attese crescenti; con il PIN giusto tornano finestra e sessioni, ancora connesse. |
| 7 | Imposta «Blocca dopo 1 minuto» e non toccare il PC | Dopo un minuto RdpManager si blocca da solo. |
| 8 | Alla schermata del PIN: *Ho dimenticato il PIN* → conferma | Il PIN è tolto, le connessioni restano, le password salvate vanno reinserite (alla connessione vengono chieste). |
| 9 | Modifica una connessione: *Qualità → Connessione lenta*, *Tieni sveglia la sessione*; connetti | Desktop remoto a 16 bit senza sfondo né animazioni; la sessione non si chiude dopo un lungo periodo senza usarla. |
| 10 | Ctrl+clic su tre connessioni, clic destro | Solo le azioni di gruppo: connetti, sposta in cartella, assegna profilo, elimina (con l'elenco dei nomi nella conferma). |
| 11 | *File → Importa connessioni da CSV* con un file di Excel (separatore `;`, accenti) | Anteprima con le colonne riconosciute, righe escluse spiegate; si importano solo quelle valide e senza doppioni. |
| 12 | **F1** fuori dalla sessione | Riepilogo di tutte le scorciatoie, divise per sezione, comprese quelle degli spazi di lavoro. |

## Prova manuale 1.5

Da fare su un PC dove RdpManager 1.4.0 è installato, con almeno due PC remoti connessi.

| # | Passo | Risultato atteso |
|---|---|---|
| 1 | Lascia che la 1.4.0 proponga l'aggiornamento e scegli «Aggiorna ora» | Si aggiorna in silenzio e si riapre con connessioni, spazi di lavoro, PIN e impostazioni di prima. |
| 2 | Clic in una sessione, **Ctrl+Alt+Invio** | Schermo intero con la barra **a sinistra** (fissa, o a comparsa se prima la barra laterale era a comparsa); in cima «Sessioni connesse». In alto non c'è niente. |
| 3 | Mouse sul bordo **in alto** dello schermo, fermo per qualche secondo | Non compare niente: il bordo in alto è della sessione remota. |
| 4 | Con la barra fissa: guarda il bordo sinistro del desktop remoto | Il desktop remoto comincia dopo la barra, non sotto: nulla è coperto; dopo circa 1 s la risoluzione si adatta. |
| 5 | Clic su una sessione nella barra, poi su «Sessione successiva» fra i pulsanti rapidi | Si passa da una sessione all'altra, come con Ctrl+Alt+1…9 e Ctrl+Alt+Destra. |
| 6 | *File → Impostazioni → Schermo intero*: posizione «A destra», modalità «A comparsa dal bordo», ritardo 500 ms | A schermo intero la sessione prende tutto; tenendo il mouse sul bordo destro per mezzo secondo la barra si apre sopra la sessione e si richiude allontanandolo. |
| 7 | Posizione «In basso», modalità «Fissa», altezza 200 | La barra sta in basso con le sezioni affiancate; il desktop remoto finisce sopra la barra. |
| 8 | Modalità «Nascosta» | Nessuna barra; Ctrl+Alt+B la apre sopra la sessione e la richiude. |
| 9 | Nelle sezioni: togli «Preferiti», sposta «Pulsanti rapidi» in cima, scegli «Con miniatura» | La barra mostra le sezioni in quell'ordine, senza preferiti, con la miniatura della sessione visibile. |
| 10 | Sezione «Connessioni»: scrivi parte di un nome, Invio | L'elenco si restringe e Invio apre (o porta a) quella connessione. |
| 11 | *File → Esporta (.rdpm)* con *Includi le impostazioni*; importa su un altro PC con *Applica anche le impostazioni* | La barra arriva con posizione, modalità e ordine delle sezioni. |

## Cosa è stato provato e cosa no

- **Provato automaticamente** (`dotnet test`): modello, salvataggio JSON e cifratura DPAPI, file `.rdp`,
  file `.rdpm` (andata e ritorno, password del file sbagliata, nessuna password in chiaro, unione senza
  doppioni), caricamento di un `connections.json` della 1.0.0, MAC e pacchetto Wake-on-LAN, scorciatoie e
  ciclo fra le sessioni, monitor; finestra principale con il controllo ActiveX vero: più sessioni,
  Ctrl+Tab, tasti inoltrati al remoto, stacca/riaggancia, avviso sul certificato e richiesta credenziali
  (server RDP finto locale); 1.1: schermo intero dell'app (finestra senza bordi sul monitor, tutti i
  monitor per le connessioni che lo chiedono), barra di passaggio (tolta nella 1.5), barra laterale fissa e a comparsa
  anche a schermo intero, Preferiti/Recenti, connetti cartella, riconnessione automatica con limite,
  riapertura all'avvio, e **Ctrl+Alt+Destra digitato davvero** (SendInput) con il fuoco nella finestra
  interna del controllo RDP; 1.2: Panoramica (solo sessioni connesse, la scheda scelta non cambia
  durante l'aggiornamento delle miniature, clic che porta alla sessione), ricerca rapida con un errore di
  battitura, vista affiancata senza ricreare i controlli ActiveX, cronologia in `history.json`, tema
  scuro e ritorno al chiaro, testi rapidi nel menu, credenziali del profilo e del gateway passate alla
  sessione, menu dell'area di notifica, scorciatoia globale (messaggio WM_HOTKEY) e chiusura nell'area di
  notifica; 1.3: Ctrl+Alt+Destra digitato davvero dentro il controllo RDP con `RegisterHotKey` (senza
  hook) e scorciatoie tolte quando l'app perde il primo piano, errore spiegato su una sessione che non trova
  l'host, riordino trascinando, log senza password né host, avviso di aggiornamento con «Più tardi», «Alla
  chiusura» e «Aggiorna ora» (sessioni salvate e riaperte, installer silenzioso con `/RELAUNCH=1`; nelle
  prove l'installer non viene eseguito), installer manomesso mai avviato. Lo stato «connessa» in queste
  prove è simulato.
- **1.5** (`dotnet test`): barra a schermo intero con valori predefiniti, conversione dalle impostazioni
  1.0-1.4 (file di esempio di ogni versione), posizione «alto» rifiutata e riportata a sinistra, ordine delle
  sezioni conservato, impostazioni nei `.rdpm`; nella finestra vera: barra fissa a sinistra, a destra e in
  basso senza sovrapporsi alla sessione, a comparsa e nascosta sopra la sessione, niente in alto, sezioni
  nell'ordine scelto, pulsanti rapidi = azioni delle scorciatoie, ricerca fra le connessioni, impostazioni;
  con il **mouse vero**: il bordo in alto non apre niente, il bordo sinistro apre la barra a comparsa.
  Schermate in `artifacts\screenshots-1.5` (prova `SaveScreenshotsOfTheV15FullScreenBar`).
- **Antivirus**: scansione di Microsoft Defender sui file della 1.2.0 e della 1.3.0 (nessuna minaccia).
- **Aggiornamento silenzioso provato a mano** (21/10/2026) sull'installazione del PC di sviluppo, che era
  alla 1.1.0: backup di programma e dati, Setup 1.3.0 della release con SHA256 verificato,
  `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1` → installer terminato, exe alla 1.3.0
  a cartella, app riaperta da sola con la finestra e l'elenco, chiusa normalmente, SHA256 dei dati utente
  identici a prima, Defender senza minacce sull'exe e sulla dll installati.
- **Aggiornamento silenzioso 1.3.0 → 1.4.0** (21/10/2026), stessa procedura sull'installazione del PC di
  sviluppo: Setup 1.4.0 della release con SHA256 verificato, installer terminato con codice 0, exe alla
  1.4.0, app riaperta da sola («RdpManager 1.4.0 - Desktop remoti») e chiusa normalmente, 0 differenze
  negli SHA256 dei dati utente, Defender senza minacce sul Setup e sull'exe installato. La prova automatica
  `verifica/installer.ps1 -Aggiornamento` va lanciata su un PC pulito (si ferma dove RdpManager è già
  installato).

- **Provato con clic e tasti veri** sull'app compilata (ottobre 2026, un monitor, connessioni finte):
  doppio clic sull'elenco, Ctrl+Alt+Invio dentro la sessione, barra in alto dal bordo e clic su una
  sessione, barra laterale a comparsa dal bordo sinistro a schermo intero, impostazioni, modifica.
- **Provato con un server vero** (un PC Windows della rete locale, ottobre 2026, senza credenziali):
  raggiungibilità, avviso sul certificato, stato e motivo nella scheda.
- **Non provato** (passi 6-17 della prima prova e tutte le prove 1.1 e 1.2): accesso con password, NLA con
  credenziali, adattamento della risoluzione e del DPI con sessione connessa, due monitor veri,
  Ctrl+Alt+Canc, incolla come digitazione, foto, audio/stampanti/microfono, Wake-on-LAN verso un PC
  vero, «Apri con mstsc» verso un PC reale; per la 1.2: miniature di sessioni davvero connesse, un
  Remote Desktop Gateway vero, testi rapidi digitati in un PC remoto, aggiornamento con un clic da una
  release vera (la verifica SHA256 è provata con un server finto).

## Pubblicare a mano (cartella o eseguibile unico)

```powershell
# a cartella (come nell'installer): RdpManager.exe con le librerie .NET
dotnet publish src/RdpManager -c Release -r win-x64 --self-contained -o artifacts\publish
# eseguibile unico portatile, non compresso
dotnet publish src/RdpManager -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o artifacts\portable
```

Entrambe girano su qualunque PC Windows x64 senza .NET installato. Dalla 1.3 il progetto non attiva più
`PublishSingleFile` da solo: la pubblicazione a cartella dà meno allarmi agli antivirus.
`build-installer.ps1` fa le due pubblicazioni e l'installer.

### Prova dell'installer e dell'aggiornamento silenzioso

```powershell
# installazione, avvio per 5 s, disinstallazione
powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1
# aggiornamento silenzioso dalla versione vecchia (/RELAUNCH=1): dati intatti, versione nuova, app riaperta
powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1 -Aggiornamento -SetupVecchio <RdpManager-Setup-1.2.0.exe>
```

Le due prove **si fermano** se RdpManager è già installato o se ci sono dati in `%APPDATA%\RdpManager`,
per non toccare un'installazione e dei dati veri: vanno lanciate su un PC pulito, una macchina virtuale o
Windows Sandbox.

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
- **Colore ed etichetta** (nella modifica): il colore tinge l'icona e la barra a schermo intero,
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
  microfono, multimonitor, Remote Desktop Gateway; la password solo se il file l'aveva cifrata per lo
  stesso utente Windows).
- **Importa dalla cronologia di mstsc** (menu File): elenca i PC che il client Desktop remoto di
  Windows ricorda per il tuo utente, con l'ultimo nome utente usato, e importa quelli spuntati nella
  cartella «Importate da mstsc» (senza doppioni). La cronologia si legge dal registro
  (`HKCU\Software\Microsoft\Terminal Server Client`) **in sola lettura e solo quando lo chiedi**; lì
  mstsc non tiene password.

### Profili di credenziali e Remote Desktop Gateway

- **Connessioni → Profili di credenziali**: un profilo tiene nome, utente, dominio e password (cifrata
  con DPAPI come quelle delle connessioni). Nella modifica di una connessione, **Credenziali** sceglie fra
  «Proprie» (utente, dominio e password della connessione) e un profilo: cambiando il profilo cambiano
  tutte le connessioni che lo usano. Eliminare un profilo lascia alle connessioni utente e dominio.
- **Gateway**: *Passa da un Remote Desktop Gateway*, l'host del gateway (anche `host:porta`) e le
  **credenziali del gateway**: le stesse della connessione oppure un profilo. Vale per la sessione nella
  scheda, per «Apri con mstsc» e per i file `.rdp` (righe `gatewayhostname`, `gatewayusagemethod`,
  `promptcredentialonce`), sia in importazione (*File → Importa file .rdp*) sia in esportazione (clic
  destro su una connessione → *Esporta come file .rdp*, senza password).
- Nei file `.rdpm` i profili seguono le stesse regole delle password: esclusi, oppure cifrati con la
  password del file; importando con «Unisci» un profilo già presente (stesso nome, utente e dominio) non
  si duplica e le connessioni importate usano quello esistente.

### Spazi di lavoro (1.4)

- *Spazi di lavoro → Salva lo spazio attuale…*: salva con un nome le sessioni aperte (nell'ordine delle
  schede), la scheda in primo piano, il monitor, lo schermo intero e la vista affiancata (con la sua
  divisione). Salvando con un nome già usato lo spazio si aggiorna.
- Si riapre dal menu *Spazi di lavoro* o con la sua **scorciatoia** (*Gestisci → Scorciatoia…*, es.
  `Ctrl+Alt+Maiusc+1`, valida anche dentro la sessione con RdpManager in primo piano). Le sessioni già
  aperte restano; le connessioni eliminate nel frattempo si saltano.
- *Gestisci*: rinomina, scorciatoia, elimina, ordine nel menu.

### Blocco con PIN (1.4)

- *File → Impostazioni → Avvio, blocco e aggiornamenti → Imposta PIN…* (da 4 a 12 cifre). Si salva solo
  un'impronta **PBKDF2-SHA256 con salt**, mai il PIN.
- **Ctrl+Alt+L** o *File → Blocca RdpManager* bloccano subito; con *Blocca dopo N minuti di inattività*
  (predefinito 10, 0 = solo a mano) si blocca da solo. Con il PIN impostato RdpManager parte già bloccato.
- Da bloccato: finestre nascoste (le sessioni restano connesse), password salvate **di nuovo cifrate** in
  memoria e non lette finché non si sblocca, nessuna sessione nuova né riconnessione, scorciatoie ferme.
  Dopo 5 PIN sbagliati si attende sempre di più.
- **Ho dimenticato il PIN**: dopo la conferma il PIN è tolto e le password salvate (connessioni e profili)
  si cancellano; le connessioni restano e le password andranno reinserite.

### Qualità, sessione sveglia, selezione multipla, CSV (1.4)

- **Qualità** (nella modifica della connessione): *Automatica* (decide il client misurando la rete, come
  prima), *Rete locale* (32 bit, tutti gli effetti), *Connessione lenta* (16 bit, niente sfondo,
  animazioni, temi e trascinamento con contenuto, compressione). Vale anche per *Apri con mstsc* e nei file
  `.rdp` importati ed esportati (`connection type`, `session bpp`, `disable wallpaper`…).
- **Tieni sveglia la sessione**: il controllo manda un segnale ogni minuto, così router e firewall non
  chiudono la sessione per inattività.
- **Selezione multipla**: Ctrl+clic aggiunge o toglie, Maiusc+clic seleziona un intervallo. Con più
  connessioni selezionate il clic destro offre solo le **azioni di gruppo**: connetti, sposta in una
  cartella (anche nuova), assegna un profilo di credenziali, elimina (la conferma elenca i nomi). Invio le
  connette, Canc le elimina.
- **Importa connessioni da CSV** (menu File): file con intestazione, separatore `;` o `,` riconosciuto da
  solo, campi tra virgolette, testo UTF-8 o come lo salva Excel. Le colonne si associano in automatico dal
  nome (*Nome, Host/Indirizzo/Server, Porta, Utente, Dominio, Cartella, Etichetta, MAC*) e si cambiano
  nell'anteprima, che mostra le righe escluse (senza host, porta o MAC non validi, doppioni).
- **F1**: riepilogo di tutte le scorciatoie (in sessione, globali, degli spazi di lavoro e della finestra).

### Riordino, errori di connessione e impostazioni (1.3)


- **Riordino**: trascina una connessione su un'altra per metterla prima di lei (anche in un'altra
  cartella), su una cartella per metterla in fondo. Da lì l'elenco segue l'ordine scelto a mano;
  *Visualizza → Ordina le connessioni per nome* (o l'opzione in *Impostazioni → Generale*) torna
  all'ordine per nome.
- **Errori di connessione spiegati**: quando una sessione si chiude per un errore, al suo posto compaiono
  il motivo, la **causa probabile** e **cosa fare** (nome non trovato, PC spento o porta chiusa, password
  sbagliata, account bloccato, certificato rifiutato, licenze, sessione presa da un altro accesso…), con
  un consiglio in più se la connessione passa da un gateway; sotto c'è il messaggio originale di Windows.
- **Impostazioni** (*File → Impostazioni*, Ctrl+,), in tre schede: *Generale* (tema, area di notifica e
  scorciatoia globale, monitor, barra laterale, ordine dell'elenco), *Scorciatoie* (tutte le combinazioni,
  con il ripristino dei predefiniti) e *Avvio e aggiornamenti* (riapertura delle sessioni, modalità degli
  aggiornamenti, cronologia, log dettagliato e cartella dei log).

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
  - **Sostituisci**: connessioni e cartelle attuali vengono cancellate e sostituite da quelle del file
    (chiede conferma); profili di credenziali e testi rapidi restano, e quelli del file si aggiungono.
- Serve per spostare l'elenco su un altro PC o un altro utente Windows: il `connections.json` non
  basta, perché le sue password DPAPI valgono solo per l'utente che le ha salvate.
- **Impostazioni dell'app** (dalla 1.3): esportando si può spuntare *Includi le impostazioni dell'app*
  (scorciatoie, tema, aggiornamenti, barra laterale…, senza monitor e sessioni da riaprire che valgono
  solo per quel PC); importando un file che le contiene si sceglie se *applicarle*.


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
- **Testi rapidi** (*Sessione → Testi rapidi*, «Invia» nella barra della sessione, «Testi» nella barra a
  schermo intero): frammenti salvati (comandi, indirizzi, firme) che un clic digita nella sessione attiva,
  con gli a capo come Invio. *Gestisci testi rapidi* li crea, modifica e ordina. Sono salvati in chiaro:
  non usarli per le password.
- **Cronologia** (*Connessioni → Cronologia*, Ctrl+H, o clic destro su una connessione): per ogni sessione
  inizio, fine, durata e motivo della disconnessione, con il tempo totale; filtro per connessione e
  «Svuota quelle mostrate». Si salva in `history.json` (ultime 2000 sessioni) e si può disattivare nelle
  impostazioni.

### Panoramica, ricerca rapida e vista affiancata

- **Panoramica** (**Ctrl+Alt+G**, *Visualizza → Panoramica*, «Panoramica» nella barra a schermo intero):
  una griglia con le miniature dal vivo delle sessioni **connesse**, sopra la finestra (anche a schermo
  intero). La miniatura della sessione visibile si aggiorna di continuo, le altre a turno ogni pochi
  secondi. Clic su una miniatura, oppure frecce e Invio, oppure il suo numero 1-9: si passa a quella
  sessione. Esc o di nuovo Ctrl+Alt+G chiude.
- **Ricerca rapida** (**Ctrl+Alt+Spazio**): si scrive parte del nome, dell'host o dell'etichetta, anche con
  le lettere a pezzi («srvpr» trova «server-prod») o con un errore di battitura; maiuscole e accenti non
  contano. Invio passa alla sessione se è già aperta, altrimenti ne apre una nuova; senza testo mostra le
  sessioni aperte, poi preferiti e recenti.
- **Vista affiancata** (**Ctrl+Alt+S**, *Visualizza → Vista affiancata* o *Affianca la sessione corrente
  a ›*): la sessione corrente e un'altra (la connessa successiva) una accanto all'altra nella stessa
  finestra; il divisore si trascina e la posizione viene ricordata. Ctrl+Alt+Destra/Sinistra passano da
  una metà all'altra; Ctrl+Alt+S, Ctrl+Alt+1…9 verso una terza sessione o chiudere una delle due
  tornano alle schede, sempre senza riconnettere.

### Area di notifica, scorciatoia globale e tema

- **Icona nell'area di notifica** (attiva di serie): clic = riporta la finestra; clic destro = menu con
  sessioni aperte, preferiti, recenti e tutte le connessioni divise per cartella, più la ricerca rapida
  ed *Esci*. Con *Chiudendo la finestra resta nell'area di notifica* la X nasconde la finestra lasciando
  le sessioni connesse; *Esci* (menu File o dell'icona) chiude davvero.
- **Scorciatoia globale** (predefinita **Ctrl+Alt+Maiusc+R**, si cambia o si disattiva in *File →
  Impostazioni*): richiama RdpManager da qualunque programma (`RegisterHotKey`). Se un altro programma
  usa già la stessa combinazione l'app lo dice nella barra di stato.
- **Tema** (*File → Impostazioni → Aspetto*): chiaro, scuro o come Windows (legge, senza modificarla, la
  modalità app di Windows); vale per finestra, menu, elenco e dialoghi.

### Schermo intero dell'app e più monitor

- **Ctrl+Alt+Invio** (o F11, o *Visualizza → Schermo intero*): la finestra di RdpManager diventa
  senza bordi e copre tutto il monitor scelto, con tutte le schede e le sessioni dentro; le
  intestazioni delle schede e le barre spariscono, la sessione corrente occupa lo schermo.
- **In alto non c'è niente** (dalla 1.5): niente barra e niente comparsa dal bordo superiore, che
  resta della sessione remota.
- **Barra a schermo intero** (dalla 1.5; a schermo intero prende il posto della barra laterale): si
  regola in *File → Impostazioni → Schermo intero*.
  - *Posizione*: a sinistra (predefinita), a destra o in basso. In alto no.
  - *Modalità*: **fissa** (sempre visibile; la sessione occupa solo lo spazio che resta, senza essere
    coperta, e con «Adatta alla finestra» la risoluzione si adatta), **a comparsa** (si apre sopra la
    sessione tenendo il mouse sul bordo del suo lato per il *ritardo di comparsa* scelto, si richiude
    allontanandolo) o **nascosta** (solo con **Ctrl+Alt+B**). Con la barra fissa Ctrl+Alt+B la nasconde e
    la rimostra.
  - *Larghezza* (in basso: altezza) e *ritardo di comparsa*.
  - *Sezioni*, spuntate e ordinate con Su/Giù: **sessioni connesse** (con miniatura o compatte; le
    connesse hanno il numero di Ctrl+Alt+1, 2, …, clic per passarci), **preferiti**, **tutte le
    connessioni** con la casella di ricerca (Invio o doppio clic per connettersi), **spazi di lavoro**,
    **pulsanti rapidi** (le stesse azioni delle scorciatoie, con la combinazione accanto) e **testi
    rapidi** (digitati nella sessione visibile). Le sezioni vuote non si mostrano.
  - Chi aggiorna dalla 1.1-1.4 trova la barra a sinistra, fissa o a comparsa come la barra laterale di
    prima, con le sessioni connesse (la vecchia barra in alto) in cima.
  - La barra a comparsa si apre per un attimo anche dopo un cambio di sessione da tastiera.
- **Ctrl+Alt+Destra / Ctrl+Alt+Sinistra** passano alla sessione connessa successiva / precedente
  (quelle non connesse si saltano), **Ctrl+Alt+1…9** alla prima, seconda… sessione connessa.
  Funzionano anche con il clic dentro la sessione remota.
- **Monitor**: *Visualizza → Monitor per lo schermo intero* (o File → Impostazioni) sceglie il
  monitor; viene ricordato e all'avvio la finestra si apre lì. Se il monitor non è più collegato si
  usa il principale. *Sposta la finestra sul monitor successivo* cambia monitor anche a schermo
  intero.
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
| Ctrl+Alt+1 … Ctrl+Alt+9 | prima … nona sessione connessa (come i numeri nella barra a schermo intero) |
| Ctrl+Alt+Invio | schermo intero sì/no |
| Ctrl+Alt+B | mostra/nascondi la barra laterale (a schermo intero: la barra a schermo intero) |
| Ctrl+Alt+G | Panoramica delle sessioni connesse |
| Ctrl+Alt+Spazio | ricerca rapida |
| Ctrl+Alt+S | vista affiancata sì/no |
| Ctrl+Alt+L | blocca RdpManager (serve un PIN) |
| scelta in *Spazi di lavoro → Gestisci* | apri uno spazio di lavoro |

Fuori dall'app, da qualunque programma: **Ctrl+Alt+Maiusc+R** riporta in primo piano RdpManager
(scorciatoia globale, si cambia nelle impostazioni).

Dalla 1.3 funzionano con `RegisterHotKey`: l'app le registra **solo mentre è in primo piano** e le
toglie appena si passa a un'altra applicazione, e il controllo RDP lascia le combinazioni di Windows al
PC locale (`KeyboardHookMode = 0`). Non c'è più nessun hook di tastiera. Se un altro programma usa già
una combinazione, la barra di stato lo dice: si cambia in *File → Impostazioni → Scorciatoie*.
*Aiuto → Scorciatoie da tastiera* mostra quelle in uso.

Nell'app:

| Tasti | Azione |
|---|---|
| Ctrl+Tab / Ctrl+Shift+Tab | scheda successiva / precedente (anche con il fuoco nella sessione) |
| Ctrl+N / Ctrl+Shift+N | nuova connessione / nuova cartella |
| Ctrl+E o F2, Ctrl+D, Canc | modifica, duplica, elimina |
| Ctrl+Shift+F | preferita sì/no |
| Ctrl+F | ricerca |
| Ctrl+I | importa file .rdp |
| Ctrl+H | cronologia delle connessioni |
| **F1** | riepilogo di tutte le scorciatoie |
| Ctrl+clic, Maiusc+clic | selezione multipla nell'elenco (poi Invio, Canc o clic destro per le azioni di gruppo) |
| Ctrl+, | impostazioni |
| Invio | connetti la connessione selezionata |
| F5 / F11 | riconnetti / schermo intero |
| Ctrl+Shift+D | stacca in una finestra |
| Ctrl+F4 | chiudi scheda |

Quando il fuoco è dentro una sessione remota, tutte le altre combinazioni vengono inviate al PC
remoto (così Ctrl+N, F5 ecc. funzionano nelle app remote).

## Dove sono salvati i dati

- Connessioni, cartelle, profili di credenziali, testi rapidi e impostazioni (scorciatoie, monitor,
  barra laterale, tema, sessioni da riaprire): `%APPDATA%\RdpManager\connections.json` (menu *File →
  Apri cartella dei dati*), insieme agli spazi di lavoro e all'impronta del PIN (mai il PIN). Un file
  dalla 1.0.0 alla 1.3.0 si carica senza perdite: i campi nuovi prendono i valori predefiniti.
- Cronologia delle sessioni: `%APPDATA%\RdpManager\history.json` (nomi, host, orari e motivi; niente
  utenti né password).
- Per provare con un elenco a parte: `RdpManager.exe --dati C:\percorso\prova.json`.
- **Log** (dalla 1.3): `%LOCALAPPDATA%\RdpManager\logs\rdpmanager.log`, a rotazione (fino a 5 file da
  1 MB). Mai password; le connessioni compaiono solo con un identificativo anonimo (`c-1a2b3c4d`) e gli
  indirizzi dei PC solo con il *log dettagliato* attivo. *Aiuto → Apri la cartella dei log*.
- Le password (delle connessioni e dei profili) **non sono mai scritte in chiaro**: nel JSON c'è solo `protectedPassword`, cifrata
  con DPAPI (`ProtectedData`, ambito *CurrentUser*). Si può decifrare solo con lo stesso utente
  Windows sullo stesso PC; copiando il file su un altro PC le password andranno reinserite.
- I file `.rdp` temporanei di *Apri con mstsc* stanno in `%TEMP%\RdpManager` e contengono la
  password solo in forma cifrata DPAPI (`password 51:b:`); le versioni recenti di mstsc possono
  comunque chiederla di nuovo.
- L'app non scrive nel registro di Windows e non salva credenziali di sistema (niente `cmdkey`). Lo
  legge soltanto, in `HKCU`, per il tema «come Windows» e, quando lo chiedi, per la cronologia di mstsc.
- L'aggiornamento scarica e verifica l'installer in `%LOCALAPPDATA%\RdpManager\updates` (vi resta solo
  l'ultimo installer verificato).


## Struttura

```
RdpManager.sln
Directory.Build.props     spegne Source Link: l'hash del commit faceva ricompilare a ogni commit (causa del
                          CS2012 nelle verifiche in parallelo); non va tolto
src/RdpManager/
  Core/   modello, salvataggio JSON, cifratura DPAPI, lettura/scrittura .rdp (senza interfaccia, testabile)
  Rdp/    hosting ActiveX (RdpClientHost : AxHost), dichiarazioni COM, avvio di mstsc
  UI/     finestra principale (schede, schermo intero, barra laterale, scorciatoie), dialoghi
tests/RdpManager.Tests/   test xUnit (Core; categoria UI = prove con finestre e controllo RDP)
installer/RdpManager.iss  script Inno Setup dell'installer
build-installer.ps1       pubblicazione + installer → dist\
verifica/                 script dei criteri di verifica e prova dell'installer
.github/workflows/ci.yml  build e test del Core su GitHub a ogni push
scripts/release.ps1       pubblicazione di una nuova versione (vedi «Pubblicare un aggiornamento»)
CHANGELOG.md              novità di ogni versione (usate come note della release)
docs/falsi-positivi.md    antivirus: diagnosi, segnalazione dei falsi positivi, firma futura
docs/firma-signpath.md    firma gratuita SignPath per i progetti open source (preparata, non richiesta)
```
