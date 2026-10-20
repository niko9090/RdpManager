; Installer di RdpManager (Inno Setup 6).
; Si compila con build-installer.ps1, che passa la versione e la cartella della pubblicazione:
;   ISCC /DAppVersion=1.0.0 /DPublishDir=<cartella con RdpManager.exe> /O<cartella output> installer\RdpManager.iss
;
; Installazione per utente, senza diritti di amministratore, in %LOCALAPPDATA%\Programs\RdpManager.
; AppId fisso: le versioni nuove si installano sopra le vecchie (stessa voce in «App installate»).
; La disinstallazione rimuove solo i file installati: %APPDATA%\RdpManager (connessioni) resta.

#ifndef AppVersion
  #error Manca la versione: compilare con /DAppVersion=X.Y.Z (vedi build-installer.ps1)
#endif
#ifndef PublishDir
  #error Manca la cartella della pubblicazione: compilare con /DPublishDir=... (vedi build-installer.ps1)
#endif

#define AppName "RdpManager"
#define AppExe "RdpManager.exe"
#define AppUrl "https://github.com/niko9090/RdpManager"

[Setup]
AppId={{7FF64784-2691-4921-9773-262F51FF85B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=niko9090
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Chiude RdpManager se è aperto durante un aggiornamento, e lo riapre dopo.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Nessuna sezione [Registry] e nessun [UninstallDelete]: i dati dell'utente in %APPDATA%\RdpManager
; non vengono mai toccati.
