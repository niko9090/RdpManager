# Prova l'AppImage di RdpManager in un container Ubuntu pulito (senza FUSE, senza ICU, senza libICE/libSM):
# deve partire (resta viva sotto Xvfb) senza «Unhandled exception». Exit 0 solo se parte.
#
# Uso:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-linux-appimage.ps1 [-AppImage <file>] [-Image ubuntu:24.04] [-Seconds 15]
# Senza -AppImage usa dist\RdpManager-<versione>-x86_64.AppImage; la ricostruisce sempre con scripts/build-linux.ps1 (mai un pacchetto vecchio).
# Senza Docker ripiega su WSL (Ubuntu), meno pulito perché la distro WSL ha già i suoi pacchetti.

param(
    [string]$AppImage = '',
    [string]$Image = 'ubuntu:24.04',
    [int]$Seconds = 15,
    [string]$Distro = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $AppImage) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Directory.Build.props'), [Text.Encoding]::UTF8)
    $version = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
    $AppImage = Join-Path $root "dist\RdpManager-$version-x86_64.AppImage"
    # Ricostruisco sempre dai sorgenti attuali: una AppImage già in dist\ (stessa versione) può essere di una build vecchia.
    if (Test-Path $AppImage) { Remove-Item -LiteralPath $AppImage -Force }
    Write-Host "Costruisco l'AppImage dai sorgenti attuali ($AppImage)" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build-linux.ps1')
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { throw "build-linux.ps1 non riuscito ($LASTEXITCODE)" }
}
if (-not (Test-Path $AppImage)) { throw "AppImage non trovata: $AppImage" }
$AppImage = (Resolve-Path $AppImage).Path

# Script che gira nel container/WSL: argomento 1 = AppImage, 2 = secondi.
$inner = @'
set -u
APP=$1
SECS=$2
W=$(mktemp -d)
cp "$APP" "$W/app.AppImage"
chmod 755 "$W/app.AppImage"
cd "$W"
if [ "$(id -u)" = 0 ] && command -v apt-get >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  # Solo quello che serve per avere un display e le librerie dichiarate dal .deb: niente fuse, icu, libice6, libsm6.
  apt-get update -qq >/dev/null 2>&1
  apt-get install -y -qq --no-install-recommends xvfb libx11-6 libfontconfig1 >/dev/null 2>&1 || { echo "apt-get install non riuscito"; exit 3; }
  # Sistema minimale vero: tolgo (anche a forza) quello che l'AppImage deve portarsi dietro o gestire da sola.
  dpkg --purge --force-depends libice6 libsm6 libfuse2 libfuse2t64 $(dpkg -l 'libicu[0-9]*' 2>/dev/null | awk '/^ii/{print $2}') >/dev/null 2>&1
  ldconfig
fi
. /etc/os-release; echo "== $PRETTY_NAME"
echo "libfuse2: $(ldconfig -p | grep -c 'libfuse.so.2') | libicu: $(ldconfig -p | grep -c libicuuc) | libICE: $(ldconfig -p | grep -c 'libICE.so.6')"

echo "== avvio diretto (informativo: senza FUSE/dev-fuse puo' fallire)"
timeout 10 ./app.AppImage --appimage-version 2>&1 | head -3

echo "== avvio con --appimage-extract-and-run sotto Xvfb per $SECS s"
export HOME=$W
Xvfb :99 -screen 0 1024x768x24 >/dev/null 2>&1 &
# Aspetto che Xvfb accetti connessioni (un sleep fisso non basta se la macchina e' carica: Avalonia muore all'avvio senza display).
for i in $(seq 1 60); do [ -S /tmp/.X11-unix/X99 ] && break; sleep 0.5; done
sleep 1
rc=1
for attempt in 1 2; do
  rm -rf squashfs-root
  DISPLAY=:99 timeout "$SECS" ./app.AppImage --appimage-extract-and-run > out.log 2>&1
  rc=$?
  [ $rc -eq 124 ] && break
  echo "-- tentativo $attempt terminato da solo (codice $rc), log:"; head -c 3000 out.log
  sleep 3
done
head -c 3000 out.log
if [ $rc -ne 124 ]; then echo "FALLITO: il programma e' terminato da solo (codice $rc) invece di restare in esecuzione"; exit 1; fi
if grep -q 'Unhandled exception' out.log; then echo "FALLITO: eccezione non gestita"; exit 1; fi
echo "OK: il programma e' rimasto in esecuzione per $SECS s"
rm -rf "$W"
exit 0
'@

$tmpDir = Join-Path ([IO.Path]::GetTempPath()) ('rdpmanager-appimage-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmpDir | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $tmpDir 'inner.sh'), (($inner -replace "`r", '') + "`n"), (New-Object Text.UTF8Encoding($false)))
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $useDocker = $false
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        docker version --format '{{.Server.Version}}' *> $null
        $useDocker = ($LASTEXITCODE -eq 0)
    }
    if ($useDocker) {
        Write-Host "Container $Image" -ForegroundColor Cyan
        $dir = Split-Path $AppImage -Parent
        $name = Split-Path $AppImage -Leaf
        docker run --rm -v "${dir}:/pkg:ro" -v "${tmpDir}:/t:ro" $Image bash /t/inner.sh "/pkg/$name" $Seconds 2>&1 | ForEach-Object { Write-Host $_ }
        $code = $LASTEXITCODE
    }
    else {
        Write-Host 'Docker non disponibile: uso WSL' -ForegroundColor Yellow
        $wslArgs = @(); if ($Distro) { $wslArgs = @('-d', $Distro) }
        $wApp = (& wsl.exe @wslArgs -- wslpath -a ($AppImage -replace '\\', '/') | Out-String).Trim()
        $wSh = (& wsl.exe @wslArgs -- wslpath -a ((Join-Path $tmpDir 'inner.sh') -replace '\\', '/') | Out-String).Trim()
        & wsl.exe @wslArgs -- bash $wSh $wApp $Seconds 2>&1 | ForEach-Object { Write-Host $_ }
        $code = $LASTEXITCODE
    }
    $ErrorActionPreference = $prev
}
finally {
    Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
}

if ($code -ne 0) { Write-Host "PROVA AppImage FALLITA ($code)" -ForegroundColor Red; exit 1 }
Write-Host 'PROVA AppImage superata' -ForegroundColor Green
exit 0
