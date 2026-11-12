; Installer di RdpManager (Inno Setup 6).
; Si compila con build-installer.ps1, che passa la versione e la cartella della pubblicazione:
;   ISCC /DAppVersion=1.3.0 /DPublishDir=<cartella della pubblicazione> /O<cartella output> installer\RdpManager.iss
; Dalla 1.3 la pubblicazione è a cartella (RdpManager.exe con le sue librerie), non un exe unico compresso:
; meno sospetta per gli antivirus.
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
VersionInfoCompany=niko9090
VersionInfoDescription=Installazione di RdpManager - gestore di desktop remoti a schede
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 niko9090
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
SetupIconFile=..\src\RdpManager\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Chiude RdpManager se è aperto durante un aggiornamento. Dopo un aggiornamento silenzioso chiesto dall'app
; (parametro /RELAUNCH=1, usato fino alla 1.6.0) lo riapre la voce di [Run] con Check: ShouldRelaunch.
; Dalla 1.6.1 l'app avvia invece il suo programma di aggiornamento, che aspetta la chiusura, installa senza
; /RELAUNCH e riapre da sé. In ogni caso InitializeSetup aspetta che RdpManager.exe sia davvero chiuso (con le
; sessioni connesse l'uscita può durare qualche secondo) e «force» chiude l'app se resta appesa.
ChangesAssociations=yes
CloseApplications=force
RestartApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Aggiornamento silenzioso avviato dall'app con «Aggiorna ora»: la riapre (riaprirà le sessioni salvate).
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

; Associazione dei file di configurazione (.rdpmconfig) per l'utente corrente (HKCU, nessun diritto di amministratore):
; il doppio clic apre «RdpManager.exe "file.rdpmconfig"», che importa la configurazione (server, VPN, registrazione).
; La disinstallazione toglie solo queste chiavi. Nessun [UninstallDelete]: i dati dell'utente in %APPDATA%\RdpManager
; non vengono mai toccati.
[Registry]
Root: HKCU; Subkey: "Software\Classes\.rdpmconfig"; ValueType: string; ValueName: ""; ValueData: "RdpManager.ConfigFile"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\RdpManager.ConfigFile"; ValueType: string; ValueName: ""; ValueData: "File di configurazione RdpManager"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\RdpManager.ConfigFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKCU; Subkey: "Software\Classes\RdpManager.ConfigFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Code]
// Vero se il file si può aprire in scrittura esclusiva: un exe in esecuzione non lo consente.
function FileIsFree(const FileName: String): Boolean;
var
  Stream: TFileStream;
begin
  Result := True;
  if not FileExists(FileName) then
    Exit;
  try
    Stream := TFileStream.Create(FileName, fmOpenReadWrite or fmShareExclusive);
    Stream.Free;
  except
    Result := False;
  end;
end;

// Il servizio Windows della VPN (RdpManagerVpn) è RdpManager.exe --vpn-service dalla cartella installata: finché gira
// tiene in uso RdpManager.exe e le librerie, l'installazione non può sostituirli e si annulla (rollback), anche
// con CloseApplications. Gli utenti hanno il permesso di fermarlo senza UAC; «net stop» aspetta che sia fermo.
// Se il servizio non c'è o è già fermo non succede nulla (il codice di uscita si ignora).
procedure StopVpnService;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\net.exe'), 'stop RdpManagerVpn', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('net stop RdpManagerVpn: codice %d', [ResultCode]))
  else
    Log('net stop RdpManagerVpn: non avviato');
end;

// Chiude i processi RdpManager.exe che girano DALLA CARTELLA INSTALLATA e tengono in uso i file da sostituire: l'app
// vecchia rimasta appesa dopo l'uscita, o un'istanza lanciata a parte. Si filtra per percorso: il programma di
// aggiornamento è anch'esso un RdpManager.exe (nella copia di riserva) e NON va fermato, quindi mai per nome.
// Il servizio della VPN (SYSTEM) non si può fermare da qui: per quello c'è StopVpnService. Chi aggiorna dalla 1.13.0
// non ha un programma di aggiornamento che li chiuda: lo fa l'installer, qui.
procedure CloseInstalledProcesses(const AppDir: String);
var
  ResultCode: Integer;
  Params: String;
begin
  Params := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-Process RdpManager -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + AppDir + '\*'' } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }"';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log(Format('Chiusi i RdpManager.exe rimasti nella cartella installata: codice %d', [ResultCode]))
  else
    Log('Chiusura dei RdpManager.exe rimasti: PowerShell non avviato');
end;

// Prima di installare: ferma il servizio della VPN; se RdpManager si sta ancora chiudendo (sessioni che si
// disconnettono) lo aspetta fino a 60 secondi, così l'installazione non trova i file in uso. Se dopo l'attesa il file
// è ancora in uso (processo appeso, servizio che non si ferma al primo colpo) chiude i processi rimasti nella cartella
// installata, riprova a fermare il servizio e aspetta ancora fino a 20 secondi; lo stato finale resta nel log.
function InitializeSetup: Boolean;
var
  Exe: String;
  Waited: Integer;
begin
  Result := True;
  if not WizardSilent then
    Exit; // installazione a mano: CloseApplications chiede all'utente; il servizio si ferma in PrepareToInstall
  StopVpnService;
  Exe := ExpandConstant('{localappdata}\Programs\{#AppName}\{#AppExe}');
  Waited := 0;
  while (not FileIsFree(Exe)) and (Waited < 60000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  if Waited > 0 then
    Log(Format('Atteso %d ms la chiusura di RdpManager', [Waited]));
  if not FileIsFree(Exe) then
  begin
    CloseInstalledProcesses(ExpandConstant('{localappdata}\Programs\{#AppName}'));
    StopVpnService;
    Waited := 0;
    while (not FileIsFree(Exe)) and (Waited < 20000) do
    begin
      Sleep(500);
      Waited := Waited + 500;
    end;
    if FileIsFree(Exe) then
      Log(Format('RdpManager.exe libero dopo la chiusura forzata (atteso altri %d ms)', [Waited]))
    else
      Log('RdpManager.exe risulta ancora in uso: l''installazione puo fallire');
  end;
end;

// Installazione a mano: dopo il clic su «Installa» (e non prima, così «Annulla» non spegne la VPN) si ferma il servizio.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  StopVpnService;
end;

// Vero se l'installer è stato avviato con /RELAUNCH=1 (o /RELAUNCH): solo allora l'app si riapre da sola.
function ShouldRelaunch: Boolean;
var
  I: Integer;
  P: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    P := Uppercase(ParamStr(I));
    if (P = '/RELAUNCH') or (P = '/RELAUNCH=1') then
      Result := True;
  end;
end;
