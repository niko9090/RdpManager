# Prova dell'installer.
#
# 1) Installazione semplice: installazione silenziosa per utente, avvio dell'exe installato per 5 s,
#    disinstallazione silenziosa, controllo che %APPDATA%\RdpManager non venga toccata.
#    powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1 [-Setup dist\RdpManager-Setup-X.Y.Z.exe]
#
# 2) Aggiornamento silenzioso (-Aggiornamento): installa la versione vecchia (-SetupVecchio, es. la 1.2.0 della
#    release), crea dati di prova inventati, aggiorna con la nuova come fa «Aggiorna ora» dell'app
#    (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER /RELAUNCH=1) e controlla che i dati restino intatti,
#    che la versione installata sia la nuova e che l'app si riapra da sola; poi disinstalla e pulisce.
#    powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1 -Aggiornamento `
#        -SetupVecchio <RdpManager-Setup-1.2.0.exe> [-Setup dist\RdpManager-Setup-1.3.0.exe]
#
# Sicurezza: la prova installa, avvia e disinstalla RdpManager nel profilo dell'utente che la lancia. Per non
# toccare una installazione vera né i dati veri, si FERMA se RdpManager è già installato o se esiste già
# %APPDATA%\RdpManager con dei dati: va lanciata su un PC (o una macchina virtuale, o Windows Sandbox) pulito.
# I dati di prova contengono solo host inventati (*.invalid) e nessuna sessione da riaprire.
param(
    [string]$Setup = '',
    [switch]$Aggiornamento,
    [string]$SetupVecchio = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Setup) { $Setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'RdpManager-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName }
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'Installer non trovato: eseguire prima build-installer.ps1' }
if (Get-Process RdpManager -ErrorAction SilentlyContinue) { throw 'RdpManager è già in esecuzione: chiuderlo prima della prova' }

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\RdpManager'
$exe = Join-Path $appDir 'RdpManager.exe'
$data = Join-Path $env:APPDATA 'RdpManager'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\RdpManager.lnk'

$ok = $true
function Check($cond, $msg) { if ($cond) { "  OK   $msg" } else { "  FAIL $msg"; $script:ok = $false } }
function Hashes($dir) { Get-ChildItem $dir -Recurse -File | ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash $_.FullName).Hash } }
function Uninstall {
    $uninst = Get-ChildItem $appDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $uninst) { return $null }
    $u = Start-Process $uninst.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    # Il disinstallatore di Inno finisce di cancellarsi in un processo figlio: si attende un poco.
    for ($i = 0; $i -lt 20 -and (Test-Path $exe); $i++) { Start-Sleep -Milliseconds 500 }
    return $u.ExitCode
}

if ($Aggiornamento) {
    # ---------- aggiornamento silenzioso dalla versione vecchia alla nuova ----------
    if (-not $SetupVecchio -or -not (Test-Path $SetupVecchio)) { throw 'Indicare -SetupVecchio con l''installer della versione precedente (es. quello della release 1.2.0)' }
    if (Test-Path $exe) { throw "RdpManager è già installato in ${appDir}: la prova lo sostituirebbe. Lanciarla su un PC pulito (macchina virtuale o Windows Sandbox)." }
    if ((Test-Path $data) -and (Get-ChildItem $data -Force -ErrorAction SilentlyContinue)) { throw "Esistono già dati in ${data}: la prova non li tocca. Lanciarla su un PC pulito (macchina virtuale o Windows Sandbox)." }

    try {
        "Installo la versione vecchia: $SetupVecchio"
        $p = Start-Process $SetupVecchio -ArgumentList '/VERYSILENT', '/CURRENTUSER', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
        Check ($p.ExitCode -eq 0) "installazione vecchia terminata con codice $($p.ExitCode)"
        $oldVersion = (Get-Item $exe).VersionInfo.FileVersion
        "  versione installata: $oldVersion"

        # Dati di prova inventati (come li scriverebbe l'app), nessuna sessione da riaprire.
        New-Item -ItemType Directory -Force $data | Out-Null
        $json = @'
{
  "version": 3,
  "settings": { "reopenSessionsOnStartup": false, "openSessions": [], "shortcuts": {}, "showTrayIcon": false, "globalHotkey": "" },
  "groups": [ "Prova" ],
  "connections": [
    { "id": "5e1f0000-0000-4000-8000-000000000001", "name": "PC di prova", "host": "pc-prova.invalid", "port": 3389,
      "username": "utente", "domain": "", "group": "Prova" }
  ]
}
'@
        [IO.File]::WriteAllText((Join-Path $data 'connections.json'), $json, (New-Object Text.UTF8Encoding($false)))
        Set-Content -Path (Join-Path $data 'sentinella-prova-aggiornamento.txt') -Value "creato dalla prova $(Get-Date -Format o)" -Encoding UTF8
        $before = Hashes $data

        "Aggiorno in silenzio con: $Setup /RELAUNCH=1"
        # Non -Wait: in Windows PowerShell aspetterebbe anche i figli, cioè l'app riaperta dall'installer.
        $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/RELAUNCH=1' -PassThru
        $p.WaitForExit()
        Check ($p.ExitCode -eq 0) "aggiornamento terminato con codice $($p.ExitCode)"
        $newVersion = (Get-Item $exe).VersionInfo.FileVersion
        Check ($newVersion -ne $oldVersion) "versione installata cambiata: $oldVersion -> $newVersion"

        $relaunched = $null
        for ($i = 0; $i -lt 30 -and -not $relaunched; $i++) {
            Start-Sleep -Milliseconds 500
            $relaunched = Get-Process RdpManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
        }
        Check ($null -ne $relaunched) "l'app si è riaperta da sola dopo l'aggiornamento (/RELAUNCH=1)"
        if ($relaunched) {
            Start-Sleep 5
            $relaunched.Refresh()
            Check (-not $relaunched.HasExited) "l'app aggiornata resta aperta per 5 s"
            # Chiusura normale (come l'utente); se non basta, forzata.
            [void]$relaunched.CloseMainWindow()
            if (-not $relaunched.WaitForExit(15000)) { Stop-Process -Id $relaunched.Id -Force; $relaunched.WaitForExit(10000) | Out-Null }
        }

        $after = Hashes $data | Where-Object { $_ -notmatch '\\(history\.json|logs\\)' }
        $missing = Compare-Object @($before) @($after) | Where-Object { $_.SideIndicator -eq '<=' }
        Check ($null -eq $missing) "dati utente intatti dopo l'aggiornamento ($(@($before).Count) file, stessi hash)"

        "Disinstallo"
        $code = Uninstall
        Check ($code -eq 0) "disinstallazione terminata con codice $code"
        Check (-not (Test-Path $exe)) "exe rimosso"
    }
    finally {
        if (Get-Process RdpManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) { Stop-Process -Name RdpManager -Force -ErrorAction SilentlyContinue }
        if (Test-Path $exe) { Uninstall | Out-Null }
        # I dati erano stati creati dalla prova (prima non c'erano): si tolgono.
        Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($ok) { 'ESITO: OK' } else { 'ESITO: FALLITO'; exit 1 }
    return
}

# ---------- installazione semplice ----------
if (Test-Path $exe) { throw "RdpManager è già installato in ${appDir}: la prova lo disinstallerebbe. Lanciarla su un PC pulito (macchina virtuale o Windows Sandbox)." }

# Sentinella nei dati utente: deve sopravvivere a installazione, avvio e disinstallazione.
$dataExisted = Test-Path $data
New-Item -ItemType Directory -Force $data | Out-Null
$sentinel = Join-Path $data 'sentinella-prova-installer.txt'
Set-Content -Path $sentinel -Value "creato dalla prova dell'installer $(Get-Date -Format o)" -Encoding UTF8
$before = Hashes $data

try {
    "Installo: $Setup"
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/CURRENTUSER', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    Check ($p.ExitCode -eq 0) "installer terminato con codice $($p.ExitCode)"
    Check (Test-Path $exe) "exe installato in $exe"
    Check (Test-Path (Join-Path $appDir 'RdpManager.dll')) "installazione a cartella (RdpManager.dll accanto all'exe)"
    Check (Test-Path $startMenu) "collegamento nel menu Start ($startMenu)"
    $uninst = Get-ChildItem $appDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check ($null -ne $uninst) "programma di disinstallazione presente"

    "Avvio l'exe installato per 5 secondi"
    $app = Start-Process $exe -PassThru
    Start-Sleep 5
    $app.Refresh()
    Check (-not $app.HasExited) "RdpManager installato aperto dopo 5 s"
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit(10000) | Out-Null }

    "Disinstallo"
    $code = Uninstall
    Check ($code -eq 0) "disinstallazione terminata con codice $code"
    Check (-not (Test-Path $exe)) "exe rimosso"
    Check (-not (Test-Path $startMenu)) "collegamento nel menu Start rimosso"

    $after = Hashes $data | Where-Object { $_ -notmatch '\\(history\.json|logs\\)' }
    Check ((Compare-Object @($before) @($after)) -eq $null) "%APPDATA%\RdpManager invariata ($(@($after).Count) file, stessi hash)"
}
finally {
    Remove-Item $sentinel -ErrorAction SilentlyContinue
    if (-not $dataExisted -and -not (Get-ChildItem $data -Force -ErrorAction SilentlyContinue)) { Remove-Item $data -ErrorAction SilentlyContinue }
}
if ($ok) { 'ESITO: OK' } else { 'ESITO: FALLITO'; exit 1 }
