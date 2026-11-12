# Prova il pacchetto .rpm di RdpManager in una Fedora pulita (container fedora:latest con Docker).
# Usa dist\rdpmanager-<versione>-1.x86_64.rpm; se manca lo costruisce con scripts\build-linux.ps1.
# Controlli: Requires (statici, via rpm in WSL), file e permessi, poi dnf install in Fedora e avvio sotto xvfb
# (il programma deve restare vivo alcuni secondi, senza librerie mancanti). Exit 0 solo se tutto va.
# Se Docker non e disponibile fa soltanto i controlli statici, lo dice chiaramente e finisce con exit 0
# stampando un ATTENZIONE in giallo (con -SoloStatico salta direttamente Docker).
#
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-linux-rpm.ps1 [-Rpm percorso.rpm] [-SoloStatico]

param(
    [string]$Rpm = '',
    [switch]$SoloStatico,
    [int]$Secondi = 8,
    [string]$Immagine = 'fedora:latest'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$fail = New-Object System.Collections.Generic.List[string]
function Ko([string]$m) { Write-Host "KO  $m" -ForegroundColor Red; $fail.Add($m) }
function Ok([string]$m) { Write-Host "OK  $m" -ForegroundColor Green }

# --- il pacchetto ---
if (-not $Rpm) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Directory.Build.props'), [Text.Encoding]::UTF8)
    $ver = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
    $Rpm = Join-Path $root "dist\rdpmanager-$ver-1.x86_64.rpm"
    if (-not (Test-Path $Rpm)) {
        Write-Host "Manca ${Rpm}: lo costruisco..." -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-linux.ps1') -Version $ver
        if ($LASTEXITCODE -ne 0) { throw 'build-linux.ps1 non riuscito' }
    }
}
if (-not (Test-Path $Rpm)) { throw "rpm non trovato: $Rpm" }
$Rpm = (Resolve-Path $Rpm).Path
Write-Host "rpm: $Rpm"

# --- controlli statici in WSL (rpm) ---
function Wsl([string]$cmd) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & wsl.exe -- bash -c $cmd 2>&1 | Out-String; return @{ Code = $LASTEXITCODE; Out = ($o -replace "[`0`r]", '') } }
    finally { $ErrorActionPreference = $prev }
}
$wslRpm = (& wsl.exe -- wslpath -a ($Rpm -replace '\\', '/') | Out-String).Trim()
$r = Wsl "command -v rpm >/dev/null || (echo 'rpm manca in WSL' >&2; exit 9)"
if ($r.Code -ne 0) { Ko "rpm non disponibile in WSL: $($r.Out)" }
else {
    $req = (Wsl "rpm -qpR '$wslRpm'").Out
    $lst = (Wsl "rpm -qplv '$wslRpm'").Out
    $scr = (Wsl "rpm -qp --scripts '$wslRpm'").Out
    foreach ($n in 'freerdp', 'libsecret', 'libicu', 'krb5-libs', 'openssl-libs', 'libX11', 'fontconfig') {
        if ($req -match "(?m)^$([regex]::Escape($n))\b") { Ok "Requires $n" } else { Ko "Requires mancante: $n" }
    }
    foreach ($p in 'rdpmanager/RdpManager.Linux', 'usr/bin/rdpmanager', 'rdpmanager.desktop', 'rdpmanager-rdpmconfig.xml', 'rdpmanager.png') {
        if ($lst -match [regex]::Escape($p)) { Ok "file $p" } else { Ko "file mancante nel pacchetto: $p" }
    }
    if ($lst -match '(?m)^-rwxr-xr-x.*/opt/rdpmanager/RdpManager\.Linux$') { Ok 'RdpManager.Linux eseguibile' } else { Ko 'RdpManager.Linux non e 755' }
    if ($lst -match '(?m)^lrwxrwxrwx.*/usr/bin/rdpmanager ->') { Ok '/usr/bin/rdpmanager e un link' } else { Ko '/usr/bin/rdpmanager non e un collegamento' }
    if ($scr -match 'update-desktop-database') { Ok 'script %post presente' } else { Ko 'manca lo script %post' }
}

if ($fail.Count -gt 0) { Write-Host "`nControlli statici falliti: $($fail.Count)" -ForegroundColor Red; exit 1 }
if ($SoloStatico) { Write-Host "`nControlli statici OK (solo statico richiesto)." -ForegroundColor Yellow; exit 0 }

# --- Docker (se il demone e spento prova ad avviare Docker Desktop) ---
function DockerPronto { & docker info *> $null; return ($LASTEXITCODE -eq 0) }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host 'ATTENZIONE: docker non installato: fatti SOLO i controlli statici, nessuna prova in Fedora.' -ForegroundColor Yellow; exit 0
}
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
if (-not (DockerPronto)) {
    $dd = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (Test-Path $dd) {
        Write-Host 'Avvio Docker Desktop...' -ForegroundColor Cyan
        Start-Process $dd
        for ($i = 0; $i -lt 40 -and -not (DockerPronto); $i++) { Start-Sleep 5 }
    }
}
$pronto = DockerPronto
$ErrorActionPreference = $prev
if (-not $pronto) {
    Write-Host 'ATTENZIONE: il demone Docker non risponde: fatti SOLO i controlli statici, nessuna prova in Fedora.' -ForegroundColor Yellow; exit 0
}

# --- prova in Fedora pulita ---
$dir = Split-Path $Rpm -Parent
$name = Split-Path $Rpm -Leaf
$inner = @"
set -e
dnf -y -q install /pkg/$name xorg-x11-server-Xvfb
rpm -V rdpmanager || true
echo '--- ldd (librerie mancanti) ---'
miss=`$(ldd /opt/rdpmanager/libhostfxr.so /opt/rdpmanager/libcoreclr.so 2>&1 | grep 'not found' || true)
if [ -n "`$miss" ]; then echo "`$miss"; echo 'LIBRERIE MANCANTI'; exit 3; fi
test -x /usr/bin/rdpmanager
echo '--- avvio sotto xvfb ---'
set +e
timeout $Secondi xvfb-run -a rdpmanager > /tmp/out.log 2>&1
code=`$?
set -e
cat /tmp/out.log
if [ `$code -eq 124 ]; then echo 'VIVO dopo $Secondi s'; exit 0; fi
echo "Terminato subito con codice `$code"; exit 4
"@
$inner = $inner -replace "`r", ''
Write-Host "docker run $Immagine ..." -ForegroundColor Cyan
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& docker run --rm -v "${dir}:/pkg:ro" $Immagine bash -c $inner 2>&1 | ForEach-Object { Write-Host $_ }
$code = $LASTEXITCODE
$ErrorActionPreference = $prev
if ($code -ne 0) { Write-Host "`nKO: prova in Fedora fallita (codice $code)" -ForegroundColor Red; exit 1 }
Write-Host "`nOK: installazione e avvio in $Immagine riusciti." -ForegroundColor Green
exit 0
