# Antivirus: falsi positivi e firma del codice

RdpManager non è firmato con un certificato di firma del codice. Per questo alcuni antivirus e Windows
SmartScreen possono bloccare l'installer o l'app, soprattutto subito dopo una release (file nuovi e senza
«reputazione»). Questa pagina spiega cosa fa l'app, come segnalare il falso positivo e come firmare in futuro.

## Cosa è stato fatto nella 1.3.0 per ridurre i segnali sospetti

| Prima (fino alla 1.2) | Dalla 1.3.0 |
|---|---|
| Hook di tastiera di basso livello (`SetWindowsHookEx` con `WH_KEYBOARD_LL`) per le scorciatoie dentro la sessione | **Tolto.** Le scorciatoie si registrano con `RegisterHotKey` solo mentre RdpManager è in primo piano e si tolgono appena passa in secondo piano; il controllo RDP ha `KeyboardHookMode = 0` |
| Installer con dentro un unico exe autoestraente (file unico .NET) | Installer con la **pubblicazione a cartella** (`RdpManager.exe` e le librerie .NET in chiaro); l'exe portatile resta un file unico **non compresso** |
| Metadati minimi | Metadati completi nell'exe (azienda, prodotto, descrizione, copyright, versione) e nell'installer (`VersionInfo*` di Inno Setup) |
| Manifest predefinito | Manifest esplicito con `requestedExecutionLevel level="asInvoker"`: nessuna richiesta di diritti di amministratore |
| Aggiornamento con conferma | Sempre con conferma; l'installer si scarica in `%LOCALAPPDATA%\RdpManager\updates`, si verifica con `SHA256SUMS.txt` della release e si esegue solo se lo SHA256 coincide (ricontrollato subito prima dell'avvio) |

Nessun offuscamento, nessun packer, nessuna scrittura nel registro, nessuna modifica alle credenziali di
Windows. Il codice è pubblico: <https://github.com/niko9090/RdpManager>.

### Diagnosi in locale (21/10/2026)

Microsoft Defender (firme 1.461.135.0), scansione personalizzata del singolo file:

```
"C:\Program Files\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File <file> -DisableRemediation
```

| File | Esito | Codice di uscita |
|---|---|---|
| `RdpManager-Setup-1.2.0.exe` (release) | nessuna minaccia | 0 |
| `RdpManager-1.2.0-win-x64.exe` (release) | nessuna minaccia | 0 |
| `RdpManager-Setup-1.3.0.exe` e `RdpManager.exe` a cartella | nessuna minaccia (vedi CHANGELOG 1.3.0) | 0 |

Nessuna firma statica di Defender riconosce i file come nocivi: i blocchi visti su altri PC vengono quasi
certamente dalla **reputazione** (SmartScreen, «file scaricato raramente», editore sconosciuto) e
dall'**analisi euristica o in cloud** di alcuni antivirus, che guarda proprio i comportamenti tolti sopra
(hook di tastiera, exe autoestraenti). Nessun file è stato caricato su VirusTotal o su altri servizi esterni.

## Segnalare il falso positivo

Prima di segnalare: scarica il file dalla pagina della release e controlla lo SHA256 con quello di
`SHA256SUMS.txt` della stessa release (in PowerShell: `Get-FileHash .\RdpManager-Setup-X.Y.Z.exe`).

### Microsoft (Defender e SmartScreen)

Modulo: <https://www.microsoft.com/wdsi/filesubmission> → «Software developer» (chi pubblica il programma)
oppure «Home customer» → prodotto «Microsoft Defender Antivirus» (o «Microsoft Defender SmartScreen» se il
blocco è la finestra blu «Windows ha protetto il PC») → «Incorrectly detected as malware/malicious».

Testo pronto (adattare versione e nome della minaccia mostrata):

> **Oggetto:** False positive — RdpManager X.Y.Z (open source Remote Desktop client manager)
>
> RdpManager is an open-source Windows desktop application (C# .NET 8, WinForms) that manages Remote Desktop
> connections and shows the sessions in tabs, hosting Microsoft's own RDP ActiveX control (mstscax.dll).
> Source code and releases: https://github.com/niko9090/RdpManager (file: RdpManager-Setup-X.Y.Z.exe,
> SHA256: <hash from SHA256SUMS.txt>).
> The installer is built with Inno Setup and installs per user under %LOCALAPPDATA%\Programs\RdpManager
> without administrator rights. The app does not install keyboard hooks (removed in 1.3.0; it uses
> RegisterHotKey only while it is in the foreground), does not write to the registry, does not modify
> Windows credentials, and is not packed or obfuscated. Passwords are stored encrypted with DPAPI for the
> current user. Network access: the RDP connections chosen by the user, a TCP reachability check on the RDP
> port, Wake-on-LAN broadcast on user request, and the GitHub API to check for updates.
> Detection name reported: <nome mostrato dall'antivirus>. Please review and remove the detection.

### Altri antivirus (moduli ufficiali per i falsi positivi)

| Antivirus | Dove segnalare |
|---|---|
| Avast / AVG | <https://www.avast.com/false-positive-file-form.php> |
| Avira | <https://support.avira.com/hc/en-us/articles/360000824509> (modulo «False positive») |
| Bitdefender | <https://www.bitdefender.com/consumer/support/answer/29358/> |
| ESET | <https://support.eset.com/en/kb141> (invio di un falso positivo) |
| Kaspersky | <https://opentip.kaspersky.com/> → «Submit file for analysis» / richiesta di riclassificazione |
| Malwarebytes | <https://forums.malwarebytes.com/forum/42-file-detections/> |
| Norton (Gen Digital) | <https://submit.norton.com/> |
| Sophos | <https://support.sophos.com/support/s/filesubmission> |
| Trend Micro | <https://www.trendmicro.com/en_us/about/legal/detection-reevaluation.html> |

Indirizzi controllati il 21/10/2026: Avast, ESET, Kaspersky, Norton, Sophos e Trend Micro rispondono; Avira,
Bitdefender e Malwarebytes (come il modulo di Microsoft) rifiutano le richieste automatiche, quindi vanno
aperti nel browser. Se una pagina è cambiata, cercare «false positive submission» sul sito del produttore.

Lo stesso testo inglese va bene per tutti; allegare (o indicare il link a) il file della release e il suo
SHA256. Le risposte arrivano di solito in 1–5 giorni lavorativi.

### Su un PC dove l'installer è già bloccato

- SmartScreen («Windows ha protetto il PC»): «Ulteriori informazioni» → «Esegui comunque», solo dopo aver
  controllato lo SHA256.
- Defender o altro antivirus: ripristinare il file dalla quarantena e aggiungere un'esclusione **solo** per
  la cartella `%LOCALAPPDATA%\Programs\RdpManager` (non per tutto il disco), poi segnalare il falso positivo
  come sopra.

## Firma del codice (per il futuro)

La firma dà agli eseguibili un editore verificato («niko9090» invece di «Editore sconosciuto») e fa crescere la
reputazione SmartScreen. Si firmano `RdpManager.exe` (con le dll proprie, `RdpManager.dll`) prima di
compilare l'installer, poi l'installer stesso. **Nessun certificato, password o token va mai messo nel
repository**: si usano i segreti di GitHub Actions o variabili d'ambiente sul PC che fa il rilascio.

### Opzione A — Azure Trusted Signing (consigliata se si ha una sottoscrizione Azure)

1. Nel portale Azure: crea un account «Trusted Signing», completa la verifica dell'identità (persona o
   azienda) e crea un *certificate profile* di tipo *Public Trust*.
2. Assegna a un'identità (utente, o *service principal* per GitHub Actions) il ruolo «Trusted Signing
   Certificate Profile Signer».
3. Firma con `signtool` e il plug-in *Azure.CodeSigning.Dlib* (o l'azione GitHub
   `azure/trusted-signing-action`), indicando endpoint, nome dell'account e del profilo in un file
   `metadata.json` **fuori dal repository** o generato al momento dai segreti:
   ```powershell
   signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
     /dlib "<percorso>\Azure.CodeSigning.Dlib.dll" /dmdf "<percorso fuori dal repo>\metadata.json" `
     artifacts\publish\RdpManager.exe artifacts\publish\RdpManager.dll
   ```
4. In Inno Setup: `SignTool=` con lo stesso comando (definito in ISCC con `/S`), così vengono firmati anche
   l'installer e il disinstallatore.
5. Credenziali: in GitHub Actions `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET` (o meglio
   l'accesso federato OIDC, senza segreto) nei *Secrets* del repository; in locale `az login`.

### Opzione B — SignPath (gratuita per i progetti open source che la ottengono)

1. Richiedi l'adesione a SignPath Foundation (<https://signpath.org/>): firma i rilasci di progetti open
   source con un certificato della fondazione, dopo una verifica del progetto.
2. Collega il repository GitHub e definisci in SignPath il *signing policy* e l'*artifact configuration*
   (quali file firmare: exe e dll nella cartella pubblicata, poi l'installer).
3. Nel workflow di GitHub Actions carica la cartella pubblicata come artefatto e usa l'azione
   `signpath/github-action-submit-signing-request` con il token API salvato nei *Secrets*
   (`SIGNPATH_API_TOKEN`); scarica i file firmati e compila l'installer.
4. Ogni rilascio firmato va approvato in SignPath (regola della fondazione).

### Dopo la firma

- Verifica: `signtool verify /pa /v RdpManager.exe` e, in Esplora file, Proprietà → Firme digitali.
- `scripts/release.ps1` deve firmare **prima** di calcolare `SHA256SUMS.txt` (l'hash cambia con la firma).
- La reputazione SmartScreen cresce con i download firmati: i primi giorni può comparire ancora l'avviso.
