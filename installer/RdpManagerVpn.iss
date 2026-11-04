; Installer di RdpManager VPN, il client VPN per i PC dei clienti (Inno Setup 6).
; Si compila con build-vpn-client.ps1, che passa nome del PC, profilo e cartella della pubblicazione:
;   ISCC /DClientName=ufficio1 /DProfilePath=<vpn.conf> /DPublishDir=<cartella> /DAppVersion=1.8.0 /O<out> installer\RdpManagerVpn.iss
;
; Serve l'amministratore (servizio Windows). Installa in Program Files\RdpManager VPN e copia il profilo come
; {app}\vpn.conf (primo posto dove ClientPaths lo cerca), con permessi solo per SYSTEM e amministratori: il
; servizio gira come SYSTEM e lo legge; gli utenti normali no (l'icona allora non mostra l'indirizzo IP).
; Il servizio si registra con «RdpManagerVpn.exe --installa-servizio», l'avvio automatico dell'icona è la voce
; HKCU\...\Run «RdpManagerVPN» (la stessa che gestisce il menu dell'icona). La disinstallazione usa
; «--rimuovi --tutto»: servizio, voce di avvio e %ProgramData%\RdpManagerVPN spariscono, poi Inno toglie i file.

#ifndef AppVersion
  #error Manca la versione: compilare con /DAppVersion=X.Y.Z (vedi build-vpn-client.ps1)
#endif
#ifndef PublishDir
  #error Manca la cartella della pubblicazione: compilare con /DPublishDir=...
#endif
#ifndef ProfilePath
  #error Manca il profilo: compilare con /DProfilePath=<file.conf>
#endif
#ifndef ClientName
  #error Manca il nome del PC: compilare con /DClientName=<nome>
#endif

#define AppName "RdpManager VPN"
#define AppExe "RdpManagerVpn.exe"

[Setup]
AppId={{3B1E6C52-8A4D-4F0B-9D27-5C61A7E0F4B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=niko9090
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Installazione di RdpManager VPN per {#ClientName}
VersionInfoProductName={#AppName}
PrivilegesRequired=admin
DefaultDirName={autopf}\RdpManager VPN
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=RdpManagerVPN-Setup-{#ClientName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ProfilePath}"; DestDir: "{app}"; DestName: "vpn.conf"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; Avvio automatico dell'icona all'accesso (stesso nome e formato di AutoStartManager); si toglie anche dal menu dell'icona.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RdpManagerVPN"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Run]
; Profilo leggibile solo da SYSTEM e amministratori (SID noti: funziona in ogni lingua di Windows).
Filename: "{sys}\icacls.exe"; Parameters: """{app}\vpn.conf"" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F"; Flags: runhidden waituntilterminated; StatusMsg: "Protezione del profilo..."
Filename: "{app}\{#AppExe}"; Parameters: "--installa-servizio"; Flags: runhidden waituntilterminated; StatusMsg: "Installazione del servizio VPN..."
; L'icona parte con l'utente che ha avviato l'installazione (non con l'amministratore dell'UAC).
Filename: "{app}\{#AppExe}"; Description: "Avvia {#AppName}"; Flags: nowait postinstall runasoriginaluser

[UninstallRun]
; Prima il servizio, il profilo in ProgramData e la voce di avvio; poi si chiude l'icona così i file si possono cancellare.
Filename: "{app}\{#AppExe}"; Parameters: "--rimuovi --tutto"; Flags: runhidden waituntilterminated; RunOnceId: "RimuoviServizio"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "ChiudiIcona"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// Reinstallazione sopra una versione già presente: servizio e icona vanno fermati, altrimenti i file sono in uso.
// --installa-servizio riconfigura e riavvia il servizio.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop RdpManagerVpnClient', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;
