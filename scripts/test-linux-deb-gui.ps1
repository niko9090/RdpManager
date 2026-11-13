# Prova del .deb come lo installa il gestore grafico (doppio clic: Ubuntu App Center, GNOME Software, Mint Gestore applicazioni).
# In un container Ubuntu 24.04 pulito:
#   1. struttura del pacchetto, desktop-file-validate e lintian (fallisce sugli errori gravi «E:»);
#   2. installazione con PackageKit (`pkcon install-local -y`, con bus di sistema e packagekitd avviati) - se PackageKit
#      nel container non regge, con gdebi (aptdaemon/Mint) -, controllo di file e librerie native, rimozione.
# Il .deb si prende da -Deb, altrimenti da dist\ (versione di src\Directory.Build.props); se manca, o con -Build,
# si costruiscono i pacchetti con scripts/build-linux.ps1. Docker Desktop, se spento, si avvia da solo.
#
# Uso:   powershell -NoProfile -File scripts/test-linux-deb-gui.ps1
#        ... -Deb C:\percorso\rdpmanager_1.14.3_amd64.deb -Images ubuntu:24.04,debian:12
# Non installa nulla su Windows: tutto avviene dentro i container.

param(
    [string]$Deb = '',
    [string[]]$Images = @('ubuntu:24.04'),   # per provare anche altri sistemi: -Images ubuntu:24.04,debian:12,ubuntu:22.04
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Deb) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Directory.Build.props'), [Text.Encoding]::UTF8)
    $version = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
    $Deb = Join-Path $root "dist\rdpmanager_${version}_amd64.deb"
    if ($Build -or -not (Test-Path $Deb)) {
        Write-Host 'Costruisco i pacchetti Linux...' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'build-linux.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Host 'Costruzione non riuscita'; exit 2 }
    }
}
if (-not (Test-Path $Deb)) { Write-Host "Manca il pacchetto: $Deb"; exit 2 }
$Deb = (Resolve-Path $Deb).Path
Write-Host "Pacchetto: $Deb" -ForegroundColor Cyan

$work = Join-Path ([IO.Path]::GetTempPath()) ('rdpmanager-guitest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Copy-Item -LiteralPath $Deb -Destination (Join-Path $work 'pkg.deb')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-linux-deb-gui.sh') -Destination (Join-Path $work 'install.sh')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-linux-lint.sh') -Destination (Join-Path $work 'lint.sh')

function Test-DockerOk {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
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
    if (-not (Test-DockerOk)) { Write-Host 'Docker non disponibile: impossibile provare.'; exit 2 }
    $env:MSYS_NO_PATHCONV = '1'
    foreach ($img in $Images) {
        foreach ($step in @('lint', 'install')) {
            Write-Host "`n=== $img ($step)" -ForegroundColor Cyan
            docker run --rm -v "${work}:/t:ro" $img bash -c "tr -d '\r' < /t/$step.sh > /tmp/run.sh; bash /tmp/run.sh /t/pkg.deb" 2>&1 | ForEach-Object { Write-Host $_ }
            if ($LASTEXITCODE -ne 0) { $failed += "$img ($step)" }
        }
    }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed.Count -gt 0) {
    Write-Host "`nPROVA FALLITA su: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "`nPROVA RIUSCITA: il .deb passa lintian e si installa come dal gestore grafico (PackageKit o, se nel container non regge, gdebi: vedi le righe «installazione via»)." -ForegroundColor Green
exit 0
