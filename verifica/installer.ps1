# Prova dell'installer: installazione silenziosa per utente, avvio dell'exe installato per 5 s,
# disinstallazione silenziosa, controllo che %APPDATA%\RdpManager non venga toccata.
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File verifica/installer.ps1 [-Setup dist\RdpManager-Setup-X.Y.Z.exe]
param([string]$Setup = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Setup) { $Setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'RdpManager-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName }
if (-not $Setup -or -not (Test-Path $Setup)) { throw 'Installer non trovato: eseguire prima build-installer.ps1' }
if (Get-Process RdpManager -ErrorAction SilentlyContinue) { throw 'RdpManager è già in esecuzione: chiuderlo prima della prova' }

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\RdpManager'
$exe = Join-Path $appDir 'RdpManager.exe'
$data = Join-Path $env:APPDATA 'RdpManager'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\RdpManager.lnk'

# Sentinella nei dati utente: deve sopravvivere a installazione, avvio e disinstallazione.
$dataExisted = Test-Path $data
New-Item -ItemType Directory -Force $data | Out-Null
$sentinel = Join-Path $data 'sentinella-prova-installer.txt'
Set-Content -Path $sentinel -Value "creato dalla prova dell'installer $(Get-Date -Format o)" -Encoding UTF8
$before = Get-ChildItem $data -Recurse -File | ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash $_.FullName).Hash }

$ok = $true
function Check($cond, $msg) { if ($cond) { "  OK   $msg" } else { "  FAIL $msg"; $script:ok = $false } }

try {
    "Installo: $Setup"
    $p = Start-Process $Setup -ArgumentList '/VERYSILENT', '/CURRENTUSER', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    Check ($p.ExitCode -eq 0) "installer terminato con codice $($p.ExitCode)"
    Check (Test-Path $exe) "exe installato in $exe"
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
    $u = Start-Process $uninst.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -PassThru -Wait
    Check ($u.ExitCode -eq 0) "disinstallazione terminata con codice $($u.ExitCode)"
    # Il disinstallatore di Inno finisce di cancellarsi in un processo figlio: si attende un poco.
    for ($i = 0; $i -lt 20 -and (Test-Path $exe); $i++) { Start-Sleep -Milliseconds 500 }
    Check (-not (Test-Path $exe)) "exe rimosso"
    Check (-not (Test-Path $startMenu)) "collegamento nel menu Start rimosso"

    $after = Get-ChildItem $data -Recurse -File | ForEach-Object { '{0}|{1}|{2}' -f $_.FullName, $_.Length, (Get-FileHash $_.FullName).Hash }
    Check ((Compare-Object @($before) @($after)) -eq $null) "%APPDATA%\RdpManager invariata ($(@($after).Count) file, stessi hash)"
}
finally {
    Remove-Item $sentinel -ErrorAction SilentlyContinue
    if (-not $dataExisted -and -not (Get-ChildItem $data -Force -ErrorAction SilentlyContinue)) { Remove-Item $data -ErrorAction SilentlyContinue }
}
if ($ok) { 'ESITO: OK' } else { 'ESITO: FALLITO'; exit 1 }
