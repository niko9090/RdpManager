# Prova di installazione del pacchetto .deb su sistemi Linux puliti: apt install, librerie native, avvio dell'app
# con display virtuale (Xvfb), rimozione. Esce con codice diverso da 0 se anche una sola prova fallisce.
#
# Sistema provato di default (container Docker, immagine ufficiale): ubuntu:24.04; con -Images anche ubuntu:22.04 e debian:12
# (Linux Mint 21/22 sono basati su Ubuntu 22.04/24.04). Senza Docker usa un chroot Ubuntu 24.04 creato in WSL con debootstrap.
# Il .deb si prende da -Deb, altrimenti da dist\ (versione di src\Directory.Build.props); ma senza -Deb
# i pacchetti si ricostruiscono sempre con scripts/build-linux.ps1.
#
# Uso:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-linux-install.ps1
#        ... -Deb C:\percorso\rdpmanager_1.14.4_amd64.deb -Images ubuntu:24.04,debian:12
#        ... -Build        (ricostruisce prima)
# Non installa nulla su Windows: tutto avviene dentro i container / il chroot WSL.

param(
    [string]$Deb = '',
    [string[]]$Images = @('ubuntu:24.04'),   # di default una sola Ubuntu pulita; per tutte: -Images ubuntu:24.04,ubuntu:22.04,debian:12
    [switch]$Build,
    [switch]$NoDocker
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$inner = Join-Path $PSScriptRoot 'test-linux-install.sh'

if (-not $Deb) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Directory.Build.props'), [Text.Encoding]::UTF8)
    $version = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
    $Deb = Join-Path $root "dist\rdpmanager_${version}_amd64.deb"
    # Ricostruisco sempre dai sorgenti attuali (un .deb già in dist\ della stessa versione può essere vecchio); -Build resta per compatibilità.
    if (Test-Path $Deb) { Remove-Item -LiteralPath $Deb -Force }
    Write-Host 'Costruisco i pacchetti Linux...' -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot 'build-linux.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host 'Costruzione non riuscita'; exit 2 }
}
if (-not (Test-Path $Deb)) { Write-Host "Manca il pacchetto: $Deb"; exit 2 }
$Deb = (Resolve-Path $Deb).Path
Write-Host "Pacchetto: $Deb" -ForegroundColor Cyan

# I file della prova vanno in una cartella temporanea con il solo .deb e lo script (montata in sola lettura).
$work = Join-Path ([IO.Path]::GetTempPath()) ('rdpmanager-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item -LiteralPath $Deb -Destination (Join-Path $work 'pkg.deb')
Copy-Item -LiteralPath $inner -Destination (Join-Path $work 'run.sh')

function Test-DockerOk {
    if ($NoDocker -or -not (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
    docker info *> $null
    if ($LASTEXITCODE -eq 0) { return $true }
    # Docker Desktop installato ma spento: lo avvio e aspetto fino a 2 minuti.
    $exe = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
    if (Test-Path $exe) {
        Start-Process $exe
        for ($i = 0; $i -lt 24; $i++) {
            Start-Sleep -Seconds 5
            docker info *> $null
            if ($LASTEXITCODE -eq 0) { return $true }
        }
    }
    return $false
}

$failed = @()
$ErrorActionPreference = 'Continue'
try {
    if (Test-DockerOk) {
        foreach ($img in $Images) {
            Write-Host "`n=== $img (Docker)" -ForegroundColor Cyan
            $env:MSYS_NO_PATHCONV = '1'
            docker run --rm -v "${work}:/t:ro" $img bash -c "tr -d '\r' < /t/run.sh > /tmp/run.sh; bash /tmp/run.sh /t/pkg.deb" 2>&1 | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) { $failed += $img }
        }
    }
    elseif (Get-Command wsl.exe -ErrorAction SilentlyContinue) {
        Write-Host "`n=== ubuntu noble (chroot in WSL)" -ForegroundColor Cyan
        $wslWork = (& wsl.exe -u root -- wslpath -a ($work -replace '\\', '/') | Out-String).Trim()
        $chroot = @'
set -e
W="$1"; C=/var/tmp/rdpmanager-test-noble
if [ ! -d "$C/usr" ]; then
  command -v debootstrap >/dev/null || { export DEBIAN_FRONTEND=noninteractive; apt-get update -qq; apt-get install -y -qq debootstrap >/dev/null; }
  debootstrap --variant=minbase noble "$C" http://archive.ubuntu.com/ubuntu >/dev/null
fi
mkdir -p "$C/t" "$C/proc"
cp "$W/pkg.deb" "$C/t/pkg.deb"; tr -d '\r' < "$W/run.sh" > "$C/t/run.sh"
mount -t proc proc "$C/proc" 2>/dev/null || true
trap 'umount "$C/proc" 2>/dev/null || true' EXIT
set +e
chroot "$C" bash /t/run.sh /t/pkg.deb
'@
        $tmp = Join-Path $work 'chroot.sh'
        [IO.File]::WriteAllText($tmp, (($chroot -replace "`r", '') + "`n"), (New-Object Text.UTF8Encoding($false)))
        $wslTmp = (& wsl.exe -u root -- wslpath -a ($tmp -replace '\\', '/') | Out-String).Trim()
        & wsl.exe -u root -- bash $wslTmp $wslWork 2>&1 | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) { $failed += 'ubuntu noble (WSL)' }
    }
    else {
        Write-Host 'Né Docker né WSL disponibili: impossibile provare.'
        exit 2
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed.Count -gt 0) {
    Write-Host "`nPROVA FALLITA su: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nPROVA RIUSCITA: il .deb si installa con apt e l'app parte." -ForegroundColor Green
exit 0
