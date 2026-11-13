; Installer di RdpManager VPN, il client VPN per i PC dei clienti (Inno Setup 6).
; Si compila con build-vpn-client.ps1, che passa la versione e la cartella della pubblicazione:
;   ISCC /DPublishDir=<cartella> /DAppVersion=1.8.0 /O<out> installer\RdpManagerVpn.iss
; Senza altro esce l'installer GENERICO dist\RdpManagerVPN-Setup.exe: uguale per tutti i PC, senza segreti dentro.
; Modalità vecchia (un profilo già pronto per un solo PC): si aggiunge /DProfilePath=<vpn.conf> /DClientName=<nome>
; e il nome del file diventa RdpManagerVPN-Setup-<nome>.exe.
;
; Serve l'amministratore (servizio Windows). Installa in Program Files\RdpManager VPN. La configurazione arriva dal file
; .rdpmconfig preparato da chi gestisce il server, in tre modi:
;   1. un *.rdpmconfig nella stessa cartella dell'installer (lo usa da solo);
;   2. il parametro /CONFIG="percorso\file.rdpmconfig";
;   3. nessuno dei due: al primo avvio l'icona chiede di scegliere il file (e c'è la voce «Carica file di configurazione…»).
; Con il file, «RdpManagerVpn.exe --configura <file>» (già amministratore, nessun UAC) crea la chiave sul PC, registra il PC sul
; server, scrive %ProgramData%\RdpManagerVPN\vpn.conf leggibile solo da SYSTEM e amministratori, avvia il servizio e accende il
; tunnel, poi mostra il riepilogo «Fatto» / «Da completare». Il file .rdpmconfig non viene mai copiato dall'installer.
; Il servizio si registra con «RdpManagerVpn.exe --installa-servizio», l'avvio automatico dell'icona è la voce
; HKCU\...\Run «RdpManagerVPN» (la stessa che gestisce il menu dell'icona). La disinstallazione usa
; «--rimuovi --tutto»: servizio, voce di avvio e %ProgramData%\RdpManagerVPN spariscono, poi Inno toglie i file.
; Associazione .rdpmconfig: la prende RdpManager VPN solo se nessun altro programma (RdpManager) l'ha già.

#ifndef AppVersion
  #error Manca la versione: compilare con /DAppVersion=X.Y.Z (vedi build-vpn-client.ps1)
#endif
#ifndef PublishDir
  #error Manca la cartella della pubblicazione: compilare con /DPublishDir=...
#endif
#ifdef ProfilePath
  #ifndef ClientName
    #error Con /DProfilePath serve anche il nome del PC: /DClientName=<nome>
  #endif
#endif

#define AppName "RdpManager VPN"
#define AppExe "RdpManagerVpn.exe"
#define ProgId "RdpManagerVPN.ConfigFile"

[Setup]
AppId={{3B1E6C52-8A4D-4F0B-9D27-5C61A7E0F4B9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=niko9090
VersionInfoVersion={#AppVersion}
#ifdef ProfilePath
VersionInfoDescription=Installazione di RdpManager VPN per {#ClientName}
OutputBaseFilename=RdpManagerVPN-Setup-{#ClientName}
#else
VersionInfoDescription=Installazione di RdpManager VPN
OutputBaseFilename=RdpManagerVPN-Setup
#endif
VersionInfoProductName={#AppName}
PrivilegesRequired=admin
DefaultDirName={autopf}\RdpManager VPN
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
SetupIconFile=..\src\RdpManager\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef ProfilePath
Source: "{#ProfilePath}"; DestDir: "{app}"; DestName: "vpn.conf"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"

[Registry]
; Avvio automatico dell'icona all'accesso (stesso nome e formato di AutoStartManager); si toglie anche dal menu dell'icona.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RdpManagerVPN"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue
; Tipo di file .rdpmconfig: solo se libero (RdpManager, se c'è, la tiene). Il nome del tipo e il comando sono nostri.
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "File di configurazione RdpManager"; Flags: uninsdeletekey; Check: CanTakeAssociation
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Check: CanTakeAssociation
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Check: CanTakeAssociation
Root: HKCU; Subkey: "Software\Classes\.rdpmconfig"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Check: CanTakeAssociation

[Run]
#ifdef ProfilePath
; Profilo leggibile solo da SYSTEM e amministratori (SID noti: funziona in ogni lingua di Windows).
Filename: "{sys}\icacls.exe"; Parameters: """{app}\vpn.conf"" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F"; Flags: runhidden waituntilterminated; StatusMsg: "Protezione del profilo..."
#endif
Filename: "{app}\{#AppExe}"; Parameters: "--installa-servizio"; Flags: runhidden waituntilterminated; StatusMsg: "Installazione del servizio VPN..."
; Con il file di configurazione (accanto all'installer o /CONFIG): chiave, registrazione, profilo, tunnel; poi il riepilogo.
Filename: "{app}\{#AppExe}"; Parameters: "--configura ""{code:ConfigFile}"""; Check: HasConfigFile; Flags: waituntilterminated; StatusMsg: "Configurazione della VPN..."
; L'icona parte con l'utente che ha avviato l'installazione (non con l'amministratore dell'UAC).
Filename: "{app}\{#AppExe}"; Description: "Avvia {#AppName}"; Flags: nowait postinstall runasoriginaluser

[UninstallRun]
; Prima il servizio, il profilo in ProgramData e la voce di avvio; poi si chiude l'icona così i file si possono cancellare.
Filename: "{app}\{#AppExe}"; Parameters: "--rimuovi --tutto"; Flags: runhidden waituntilterminated; RunOnceId: "RimuoviServizio"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "ChiudiIcona"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  ConfigPath: String;
  ConfigSearched: Boolean;

// Il file di configurazione: /CONFIG="percorso", altrimenti il primo *.rdpmconfig accanto all'installer; '' se non c'è.
function ConfigFile(Param: String): String;
var
  Folder: String;
  Found: TFindRec;
begin
  if not ConfigSearched then
  begin
    ConfigSearched := True;
    ConfigPath := ExpandConstant('{param:CONFIG|}');
    if (ConfigPath <> '') and not FileExists(ConfigPath) then
    begin
      MsgBox('Il file indicato con /CONFIG non esiste:' + #13#10 + ConfigPath + #13#10#13#10 +
        'Si installa lo stesso: dopo potrai caricare il file dall''icona di RdpManager VPN.', mbError, MB_OK);
      ConfigPath := '';
    end
    else if ConfigPath = '' then
    begin
      Folder := ExtractFilePath(ExpandConstant('{srcexe}'));
      if FindFirst(Folder + '*.rdpmconfig', Found) then
      begin
        try
          ConfigPath := Folder + Found.Name;
        finally
          FindClose(Found);
        end;
      end;
    end;
  end;
  Result := ConfigPath;
end;

function HasConfigFile: Boolean;
begin
  Result := ConfigFile('') <> '';
end;

// .rdpmconfig libero, o già nostro (reinstallazione): altrimenti resta di chi l'ha (RdpManager).
function CanTakeAssociation: Boolean;
var
  Current: String;
begin
  Result := not RegQueryStringValue(HKCU, 'Software\Classes\.rdpmconfig', '', Current) or (Current = '') or (Current = '{#ProgId}');
end;

// Alla disinstallazione l'associazione si toglie solo se è ancora la nostra.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Classes\.rdpmconfig', '', Current) and (Current = '{#ProgId}') then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\.rdpmconfig');
end;

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
