# Parità Windows / Linux di RdpManager

Elenco delle funzioni visibili all'utente della versione Windows (`src/RdpManager`, Windows Forms) confrontate con la versione Linux (`src/RdpManager.Linux`, Avalonia, più `src/RdpManager.Platform.Linux`). Compito k-65, solo analisi: nessun codice è stato cambiato.

Stato: **P** = presente, **~** = parziale, **A** = assente, **NA** = su Linux non ha senso o non è possibile (il perché è nella colonna «Su Linux serve»).

## Punto di partenza

- `RdpManager.Core` (net8.0, senza Windows Forms) è già condiviso: modelli, archivio `connections.json`, `AppSettings`, CSV/.rdp/.rdpm, sincronizzazione, configurazione WireGuard, aggiornamenti, PIN, spazi di lavoro, ricerca, cronologia. Quasi tutta la logica delle funzioni mancanti c'è già; manca l'interfaccia Avalonia.
- Linux oggi (circa 2300 righe): finestra con albero connessioni + schede di sessione, dialoghi «Modifica connessione», «Profili di credenziali», «Impostazioni» (5 opzioni), client RDP FreeRDP (`xfreerdp`/`sdl-freerdp`) incorporato via X11 o in finestra separata, password cifrate con AES-GCM e chiave gestita da `MasterKeyProviders`, backend VPN (`LinuxWireGuard`: NetworkManager o `wg-quick` via `pkexec`) **senza interfaccia**.
- Linux non ha menu: ha solo una barra di pulsanti (`MainWindow.axaml`). Quasi ogni riga «A» richiede una voce di menu o un pulsante, quindi **i file più contesi sono `MainWindow.axaml`, `MainWindowViewModel.cs` e `SettingsWindow.axaml`** (vedi i due blocchi).

## Tabella

Percorsi Windows relativi a `src/RdpManager/UI/` salvo dove indicato; «Core» = `src/RdpManager.Core/`.

### Connessioni, gruppi, ricerca

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 1 | Albero connessioni con cartelle annidate | P | `MainForm.cs` | `ConnectionStore`, `ConnectionsDocument` | già fatto (`TreeNodeViewModel`) |
| 2 | Nuova / modifica / duplica / elimina connessione | P | `ConnectionDialog.cs`, `MainForm.cs` | `RdpConnection` | già fatto; mancano solo menu e scorciatoie (#38) |
| 3 | Nuova / rinomina / elimina cartella | P | `MainForm.cs` | `ConnectionsDocument` | già fatto |
| 4 | Campi della connessione: host, utente, dominio, password, risoluzione, dispositivi, qualità, gateway, MAC, riconnessione, preferita, etichetta, profilo credenziali | ~ | `ConnectionDialog.cs` | `RdpConnection`, `QualityProfile` | aggiungere: colore dell'etichetta, numero di tentativi di riconnessione, «Usa la risoluzione della cartella», «Usa tutti i monitor», «Mostra» password; «Tieni sveglia» è salvata ma `FreeRdpCommand` non la usa |
| 5 | Impostazioni della cartella (risoluzione ereditata, ecc.) | A | `FolderSettingsDialog.cs` | `FolderSettings` | nuovo dialogo Avalonia; l'ereditarietà va applicata anche in `FreeRdpCommand` |
| 6 | Selezione multipla: connetti / elimina / sposta in cartella | A | `MainForm.cs`, `MainForm.V12.cs` | `GroupActions` | albero con selezione multipla e le tre azioni |
| 7 | Connetti tutta la cartella | A | `MainForm.cs`, `MainForm.Sessions.cs` | `GroupActions` | voce di menu contestuale |
| 8 | Ordine delle connessioni: per nome o trascinando | ~ | `MainForm.cs`, `SettingsDialog.cs` | `ConnectionOrder`, `ConnectionsDocument.InDisplayOrder` | l'opzione c'è (Impostazioni) ma manca il trascinamento nell'albero |
| 9 | Preferite (★) | P | `ConnectionDialog.cs`, `MainForm.cs` | `RdpConnection.Favorite` | già fatto (pulsante e campo) |
| 10 | Menu «Preferiti» e «Recenti» (anche nell'icona di notifica) | A | `MainForm.V12.cs` | `ConnectionHistory` | elenco rapido nel menu |
| 11 | Ricerca nell'albero (campo Cerca, Ctrl+F) | ~ | `MainForm.cs` | `FuzzySearch` | oggi usa `c.Matches(query)` (semplice); passare a `FuzzySearch`, aggiungere Ctrl+F e Esc |
| 12 | Ricerca rapida (finestrella con elenco) | A | `QuickSearchForm.cs` | `FuzzySearch` | finestra Avalonia leggera |
| 13 | Verifica raggiungibilità dei PC | A | `MainForm.V12.cs` | `HostReachability` | solo interfaccia: usa socket TCP, nessuna dipendenza di sistema |
| 14 | Accendi (Wake-on-LAN) | ~ | `MainForm.cs` | `WakeOnLan` | il campo MAC c'è; manca il comando (UDP, portabile) |
| 15 | Cerca PC in rete (rete locale, Tailscale, altre VPN) | A | `NetworkScanForm.cs` | `NetworkScan`, `Tailscale`, `NetworkAdapters`, `DiscoveredPc` | finestra nuova; verificare che `NetworkAdapters`/`Tailscale` leggano interfacce e CLI anche su Linux |
| 16 | Profili di credenziali (elenco, nuovo, modifica, assegna) | P | `V12Dialogs.cs` | `CredentialProfile` | già fatto (`ProfilesWindow`) |
| 17 | Testi rapidi (gestione e invio alla sessione) | A | `V12Dialogs.cs`, `MainForm.V12.cs` | `QuickText` | gestione = dialogo; invio = digitazione nella sessione (come #31) |
| 18 | Cronologia sessioni: registrazione | P | `MainForm.Sessions.cs` | `ConnectionHistory` | già fatto (`MainWindowViewModel` registra se `HistoryEnabled`) |
| 19 | Cronologia sessioni: finestra «Cronologia…» | A | `V12Dialogs.cs` | `ConnectionHistory` | tabella (connessione, inizio, durata, motivo) |

### Importa, esporta, sicurezza

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 20 | Importa file .rdp | A | `MainForm.V12.cs` | `RdpFile` | selettore file Avalonia (`StorageProvider`) |
| 21 | Importa file .rdpm (backup, password del file, unisci/sostituisci) | A | `RdpmDialogs.cs` | `RdpmFile` | due dialoghi (opzioni + password del file) |
| 22 | Importa da CSV | A | `MainForm.V12.cs` | `CsvImport` | selettore file + riepilogo |
| 23 | Importa dalla cronologia di mstsc | NA | `Platform/MstscHistory.cs` | — | legge il registro di Windows: su Linux non esiste |
| 24 | Esporta tutto in .rdpm (senza/con password, con impostazioni app) | A | `RdpmDialogs.cs` | `RdpmFile` | dialogo + salvataggio; le password si ricifrano con la password del file |
| 25 | Esporta una connessione come file .rdp | A | `MainForm.V12.cs` | `RdpFile` | voce contestuale |
| 26 | Apri con mstsc | NA | `Rdp/MstscLauncher.cs` | — | `mstsc` è solo Windows; su Linux fa lo stesso la modalità «finestra separata» di FreeRDP (già presente) |
| 27 | Password salvate, cifrate per utente | P | `Platform/DpapiSecretStore.cs` | `ISecretStore`, `PasswordProtector` | già fatto (`AesGcmSecretStore`, `FileKeySecretStore`, `MasterKeyProviders`). Le password di `connections.json` non passano tra Windows e Linux (cifratura diversa): si usa .rdpm |
| 28 | PIN e blocco dell'app (blocco a tempo, «Ho dimenticato il PIN») | A | `V14Dialogs.cs`, `MainForm.V14.cs`, `SettingsDialog.cs` | `PinLock` | dialoghi imposta/sblocca, schermata di blocco, timer di inattività |
| 29 | Avviso «password non leggibile da questo utente» | ~ | `ConnectionDialog.cs` | `PasswordProtector` | messaggio nel dialogo di modifica |

### Sessione, schede, finestre

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 30 | Sessione RDP incorporata nella scheda | P | `RdpSessionView.cs`, `Rdp/RdpClientHost.cs` | `RdpConnection` | già fatto (`FreeRdpSession`, `X11EmbedHost`); su Wayland puro l'incorporamento X11 non funziona (resta la finestra separata) |
| 31 | Incolla appunti come digitazione, Ctrl+Alt+Canc, tastiera su schermo, foto della sessione | A | `RdpSessionView.cs`, `MainForm.Sessions.cs`, `Core/Touch.cs` | `Touch` | `xdotool`/`ydotool` per i tasti, cattura della finestra X11 per la foto; tastiera su schermo: usare quella del desktop (`onboard`), `TabTip`/`osk.exe` sono NA |
| 32 | Schede: chiusura con ✕ | P | `MainForm.Sessions.cs` | — | già fatto |
| 33 | Schede: Ctrl+F4, successiva/precedente, menu contestuale della scheda | A | `MainForm.cs`, `MainForm.Sessions.cs` | `Shortcuts`, `HotkeyRules` | `KeyBindings` e `ContextMenu` Avalonia |
| 34 | Riconnetti a mano (F5) | ~ | `MainForm.Sessions.cs` | — | il pulsante c'è (`ReconnectCommand`); manca F5 |
| 35 | Riconnessione automatica | ~ | `RdpSessionView.cs` | `ReconnectPolicy` | passata a `xfreerdp` (`/auto-reconnect`); manca la politica dell'app per gli errori non recuperabili |
| 36 | Stacca la sessione in una finestra / riaggancia | A | `MainForm.Sessions.cs` | — | seconda finestra Avalonia che ospita la stessa `ISessionHost` |
| 37 | Vista affiancata (due sessioni insieme) | A | `MainForm.V12.cs` | `ViewLayouts` | contenitore a due colonne; l'incorporamento X11 va ricollocato |
| 38 | Menu principale (File, Connessioni, Sessione, Visualizza, Strumenti, Spazi di lavoro, Aiuto) e menu contestuale dell'albero | A | `MainForm.cs` | — | `Menu`/`NativeMenu` Avalonia e comandi (passo 0, vedi sotto) |
| 39 | Panoramica delle sessioni (miniature) | A | `OverviewForm.cs` | — | miniature da cattura finestra X11; su Wayland limitata |
| 40 | Schermo intero e barra a schermo intero (sezioni configurabili) | A | `FullScreenSideBar.cs`, `MainForm.Sessions.cs` | `FullScreenBar` | `WindowState.FullScreen` + barra Avalonia |
| 41 | Scelta del monitor per lo schermo intero, sposta sul monitor successivo | A | `Screens.cs`, `MainForm.cs` | `MonitorLayout` | `Window.Screens` di Avalonia |
| 42 | Barra laterale fissa / a comparsa, mostra-nascondi | A | `MainForm.cs`, `ResizeGrip.cs` | `AppSettings.SidebarMode` | pannello comprimibile |
| 43 | Spazi di lavoro (salva, riapri, scorciatoia) | A | `MainForm.V12.cs`, `V14Dialogs.cs` | `Workspace` | menu «Spazi di lavoro» + dialogo; per il layout completo servono #36, #37, #41 |
| 44 | Riapri all'avvio le sessioni lasciate aperte | ~ | `MainForm.cs` | `AppSettings` | in Impostazioni la casella «Ricorda le ultime connessioni aperte» è legata a `HistoryEnabled` (cronologia): etichetta e comportamento non corrispondono, e non c'è alcuna riapertura |
| 45 | Avviso certificato del server | ~ | `RdpSessionView.cs`, `WindowAttention.cs` | `ConnectionErrors` (codice 263) | oggi `/cert:tofu` accetta alla prima volta senza chiedere e fallisce se il certificato cambia: serve un dialogo «fidati / rifiuta» |
| 46 | Finestra che richiama l'attenzione (lampeggio) per avvisi in attesa | NA | `WindowAttention.cs` | — | specifico di Win32; su Linux basta la richiesta di attenzione del desktop, se c'è |

### Tocco, scorciatoie, vassoio

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 47 | Modalità tocco (interfaccia ingrandita) | A | `TouchUi.cs`, `MainForm.Touch.cs` | `TouchMode` | stili Avalonia più grandi; il rilevamento tocco/mouse di Windows (0xFF515700) è NA, usare `PointerType` |
| 48 | Tocco con più dita dentro la sessione (zoom, scorrimento, clic destro) | A | `SessionTouchInput.cs`, `MainForm.SessionTouch.cs` | `SessionTouch` (macchina a stati portabile) | `GestureRecognizers` Avalonia; FreeRDP non inoltra il multitouch in modo uniforme: solo traduzione in mouse/scroll |
| 49 | Gesti a più dita nella finestra (impostabili) | A | `SettingsDialog.cs`, `MainForm.Touch.cs` | `SessionTouch` | come sopra + pagina in Impostazioni |
| 50 | Scorciatoie da tastiera configurabili + pagina «Scorciatoie» (F1) | A | `SettingsDialog.cs`, `Platform/Shortcuts.cs`, `ShortcutSummary.cs` | `Shortcuts`, `HotkeyRules` | convertire i tasti in `KeyGesture`; pagina di aiuto |
| 51 | Scorciatoia globale di richiamo | A | `Platform/GlobalHotkey.cs` | `HotkeyRules` | `RegisterHotKey` è Win32; su X11 si può con XGrabKey, su Wayland non è permesso: limite da dichiarare |
| 52 | Icona nell'area di notifica col menu delle connessioni, chiudi nell'area | A | `MainForm.V12.cs` | `AppSettings` | `TrayIcon` Avalonia (GNOME richiede un'estensione) |

### Impostazioni, aiuto, aggiornamenti

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 53 | Impostazioni: tema, aggiornamenti, ordine, cronologia, log dettagliato | P | `SettingsDialog.cs`, `Theme.cs` | `AppSettings` | già fatto (5 opzioni in `SettingsViewModel`) |
| 54 | Impostazioni: schede mancanti (Barra laterale, Area di notifica, Avvio, Monitor, Barra a schermo intero, Tocco, PIN, Scorciatoie, Richiamo rapido, Aggiornamenti completo) | A | `SettingsDialog.cs` | `AppSettings` | una pagina per ciascuna, legata alla funzione relativa |
| 55 | Apri cartella dei dati / dei log | A | `MainForm.cs`, `SettingsDialog.cs` | `AppPaths` | `xdg-open` |
| 56 | Informazioni, pagina GitHub | A | `MainForm.cs`, `SmallDialogs.cs` | `AppInfo` | dialogo + `xdg-open` |
| 57 | Log e registro dei guasti (AppLog, CrashLog) | ~ | `Program.cs` | `AppLog`, `CrashLog`, `ShutdownWatchdog` | la casella «Registro dettagliato» c'è; non risulta che il `Program.cs` Linux inizializzi `AppLog`/`CrashLog` |
| 58 | Controllo aggiornamenti (manuale e automatico), avviso, scaricamento verificato SHA256 | A | `UpdateOfferForm.cs`, `UpdateProgressForm.cs`, `MainForm.V12.cs` | `Updates`, `AutoUpdate`, `UpdateDownloader` | l'opzione c'è ma nessuna logica; servono file di release per Linux (`packaging/linux`) e l'avviso |
| 59 | Installazione automatica dell'aggiornamento (Inno Setup, ritorno alla versione precedente) | NA | `Core/UpdateRunner.cs`, `Program.cs` | — | l'installer è Windows. Su Linux: avviso + link o aggiornamento col gestore di pacchetti |

### Account, server, VPN

| # | Funzione | Stato | File Windows | Parte comune riusabile | Su Linux serve |
|---|---|---|---|---|---|
| 60 | Account e sincronizzazione (accedi, registrati, sincronizza ora, esci) | A | `SyncAccountDialog.cs`, `MainForm.Sync.cs` | `SyncService`, `SyncHttp`, `SyncMerge`, `SyncCrypto` | dialogo + timer; il resto è già in Core |
| 61 | «Password del server» (richiesta alla prima registrazione) | A | `SyncAccountDialog.cs` | `SyncHttp` (`invalid_registration_code`) | richiesta di testo segreto (c'è già `TextDialog`) |
| 62 | Importa configurazione (file di configurazione client) | A | `SyncAccountDialog.cs` | `ClientConfig` | selettore file |
| 62b | **File di configurazione unico `.rdpmconfig`** (k-80): server, codice, VPN, registrazione automatica del PC; accetta anche un `.conf` WireGuard | P | `MainForm.Vpn.cs` (`ImportConfigFileAsync`), `VpnForm.cs` (pulsante «Carica file di configurazione…», campi a mano in «Avanzate»), `Program.cs` (argomento), `SyncAccountDialog.cs`, installer `RdpManager.iss` (associazione per utente) | `RdpmConfigFile`, `ConfigFileImporter` (Core, condivisi) | Linux **P**: `VpnViewModel.ImportConfigFileAsync`, `LinuxConfigImport` (→ `LinuxWireGuard.EnsureConnectedAsync`), `MainWindowViewModel.ImportConfigFileAsync`/`LoadConfigFileCommand`, argomento in `Program.cs`, `MimeType` nel `.desktop` + `rdpmanager-rdpmconfig.xml` nei .deb/.rpm/AppImage |
| 63 | VPN WireGuard integrata: crea chiavi, profilo, copia frammento, stato tunnel | A | `VpnForm.cs`, `MainForm.Vpn.cs` | `VpnProfile`, `WireGuardConfig`, `WireGuardKeys` | dialogo; le chiavi sono in Core |
| 64 | Collegamento della VPN dal server (provisioning del dispositivo) | A | `VpnForm.cs`, `Vpn/VpnHelper.cs` | `VpnProvisioning`, `SyncHttp` | chiamata API + dialogo |
| 65 | Avvio/arresto del tunnel | ~ | `Vpn/WireGuardNative.cs`, `Vpn/VpnServiceHost.cs`, `Vpn/WireGuardNt*.cs` | `IWireGuardTunnel` | backend già pronto (`LinuxWireGuard`: `nmcli` o `wg-quick` con `pkexec`); manca l'interfaccia che lo usa |
| 66 | Servizio e icona del client VPN autonomo (`RdpManagerVpn`) | NA | `src/RdpManagerVpn/*` | — | servizio Windows con driver WireGuardNT; su Linux lo sostituiscono NetworkManager / `wg-quick` |
| 67 | Avvio automatico con il sistema | NA | `RdpManagerVpn/AutoStart.cs` | — | riguarda il client VPN Windows; su Linux sarebbe un `.desktop` in `autostart`, da decidere |

## Numeri

Su 67 funzioni:

- **Presenti: 10** (#1, 2, 3, 9, 16, 18, 27, 30, 32, 53)
- **Parziali: 11** (#4, 8, 11, 14, 29, 34, 35, 44, 45, 57, 65)
- **Assenti: 40**
- **Non applicabili / impossibili: 6** (#23, 26, 46, 59, 66, 67)

Da colmare: 51 (11 parziali + 40 assenti).

## Divisione in due blocchi

### Passo 0 (un solo sviluppatore, prima di dividere)

I due blocchi si pestano i piedi in `MainWindow.axaml`, `ViewModels/MainWindowViewModel.cs` e `Dialogs/SettingsWindow.axaml`. Prima di dividere serve un commit unico (anche dell'autopilota) che:

1. rende `MainWindowViewModel` `partial` e crea `ViewModels/MainWindowViewModel.A.cs` e `ViewModels/MainWindowViewModel.B.cs` (vuoti);
2. aggiunge in `MainWindow.axaml` la barra di menu completa (#38), con tutte le voci già collegate a comandi dichiarati nei due file parziali (le voci non ancora pronte restano disabilitate);
3. trasforma `SettingsWindow.axaml` in un `TabControl` con una pagina per ogni gruppo di Windows; ogni pagina è un `UserControl` in un file nuovo (`Dialogs/Settings/Pagina*.axaml`), così ogni blocco modifica solo le proprie.

Da lì ciascun blocco aggiunge comandi e pagine nei propri file e non tocca più il menu. Questo passo copre #38.

### Blocco A: connessioni, gruppi, ricerca, import/export, PIN, impostazioni (25 funzioni)

Funzioni: #4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15, 17, 19, 20, 21, 22, 24, 25, 28, 29, 44 (solo la casella e l'etichetta in Impostazioni), 54 (pagine Aspetto, Avvio, Elenco, PIN), 55, 56, 57.

File che tocca (esclusivi del blocco A):

- `src/RdpManager.Linux/ViewModels/MainWindowViewModel.A.cs` (nuovo)
- `src/RdpManager.Linux/ViewModels/ConnectionEditViewModel.cs`, `Dialogs/ConnectionEditWindow.axaml(.cs)`
- `src/RdpManager.Linux/ViewModels/SettingsViewModel.cs` e le pagine di Impostazioni di A (Aspetto, Avvio, Elenco, PIN)
- `src/RdpManager.Linux/ViewModels/ProfilesViewModel.cs`, `Dialogs/ProfilesWindow.axaml(.cs)`, `Dialogs/TextDialog.cs`
- `src/RdpManager.Linux/ViewModels/TreeNodeViewModel.cs`, `ChoiceItem.cs`, `IDialogService.cs`, `Dialogs/DialogService.cs`
- nuovi: dialoghi cartella, ricerca rapida, cerca PC in rete, cronologia, testi rapidi, .rdpm, PIN, informazioni
- `src/RdpManager.Platform.Linux/FreeRdpCommand.cs` (solo per #4 e #5: «Tieni sveglia», risoluzione ereditata, tutti i monitor)
- test: `tests/RdpManager.Linux.Tests/EditorsAndStoreTests.cs`, `MainWindowViewModelTests.cs` e nuovi file

`RdpManager.Core`: in sola lettura per entrambi; un ritocco va concordato.

### Blocco B: sincronizzazione, VPN, spazi di lavoro, schede, finestre, tocco, aggiornamenti (26 funzioni)

Funzioni: #31, 33, 34, 35, 36, 37, 39, 40, 41, 42, 43, 44 (riapertura sessioni), 45, 47, 48, 49, 50, 51, 52, 54 (pagine Area di notifica, Monitor, Barra a schermo intero, Tocco, Scorciatoie, Richiamo rapido, Aggiornamenti), 58, 60, 61, 62, 63, 64, 65.

File che tocca (esclusivi del blocco B):

- `src/RdpManager.Linux/ViewModels/MainWindowViewModel.B.cs` (nuovo)
- `src/RdpManager.Linux/MainWindow.axaml.cs`, `App.axaml`, `App.axaml.cs`, `Program.cs`, `Converters.cs`
- `src/RdpManager.Linux/Sessions/*` (`FreeRdpSession`, `FreeRdpSessionHost`, `FreeRdpSessionView`, `X11EmbedHost`, `ISessionHost`, `PlaceholderSessionHost`) e `ViewModels/SessionTabViewModel.cs`
- `src/RdpManager.Linux/Platform/*`
- `src/RdpManager.Platform.Linux/*` tranne `FreeRdpCommand.cs`: `LinuxWireGuard.cs`, `FreeRdpProcess.cs`, `ProcessRunner.cs`, `LinuxPlatformInit.cs`
- nuovi: dialoghi account, VPN, spazi di lavoro, aggiornamenti, panoramica, avviso certificato, schermo intero; pagine di Impostazioni di B
- test: `tests/RdpManager.Linux.Tests/FreeRdpSessionHostTests.cs` e nuovi file

Punto di contatto: #45 (avviso certificato) tocca `/cert:tofu` in `FreeRdpCommand.cs` (file di A). Soluzione: A espone una proprietà/parametro `CertificatePolicy` nel passo 0 oppure B fa quella sola modifica dopo l'unione di A.

### Equilibrio

- A: 25 funzioni, quasi tutte dialoghi e liste con logica già in Core: lavoro ampio ma lineare.
- B: 26 funzioni, con più rischio (X11/Wayland, tocco, VPN, aggiornamenti): meno numerose per peso, ma più difficili da provare.
- Per spostare peso senza conflitti: #15 (cerca PC in rete) e #17 (testi rapidi) sono finestre nuove e si possono dare a B.

## Da decidere con Nicholas

1. Wayland: l'incorporamento delle sessioni è solo X11. Su Wayland resta la finestra separata; se serve «uguale a Windows» anche lì, serve un altro percorso (per esempio `sdl-freerdp3` in finestra propria, senza schede).
2. Aggiornamenti su Linux: avviso + link, oppure un meccanismo vero (AppImage con aggiornamento, repository `.deb`).
3. Scorciatoia globale e icona nel vassoio dipendono dal desktop (GNOME senza estensioni, Wayland): da accettare come limite.
4. Le password di `connections.json` sono legate all'utente e al sistema: tra Windows e Linux si spostano solo con un file .rdpm; va spiegato nell'interfaccia.
