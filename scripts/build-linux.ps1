# Costruisce i pacchetti Linux linux-x64 di RdpManager.Linux: .deb, .rpm e AppImage.
# Risultato in dist\:
#   rdpmanager_<versione>_amd64.deb            Debian / Ubuntu / Mint
#   rdpmanager-<versione>-1.x86_64.rpm         Fedora / openSUSE
#   RdpManager-<versione>-x86_64.AppImage      ovunque (nessuna installazione)
#
# La pubblicazione (dotnet publish, self-contained) si fa su Windows; i pacchetti si costruiscono dentro WSL
# con packaging/linux/build-packages.sh, che richiede dpkg-deb, rpmbuild (pacchetto «rpm»), squashfs-tools e curl.
# Se mancano, lo script prova a installarli in WSL come root (apt-get). appimagetool si scarica in artifacts\tools.
#
# Uso:   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-linux.ps1 -Version 1.12.1
# Senza -Version la versione si legge da <Version> in src\Directory.Build.props (la stessa fonte dell'installer Windows).

param(
    [string]$Version = '',
    [string]$Distro = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    $text = [IO.File]::ReadAllText((Join-Path $root 'src\Directory.Build.props'), [Text.Encoding]::UTF8)
    $Version = [regex]::Match($text, '<Version>([^<]+)</Version>').Groups[1].Value.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: «$Version»" }

if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw 'WSL non trovato: i pacchetti Linux si costruiscono dentro WSL (in alternativa usare il workflow GitHub Actions linux-packages).'
}
$wslArgs = @()
if ($Distro) { $wslArgs = @('-d', $Distro) }

function Invoke-Wsl([string[]]$extra, [string]$script) {
    # Lo script passa da un file temporaneo UTF-8 senza BOM e con fine riga LF (da stdin PowerShell 5.1 aggiunge il BOM);
    # l'uscita nativa non deve far scattare ErrorActionPreference.
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('rdpmanager-wsl-' + [guid]::NewGuid().ToString('N') + '.sh')
    [IO.File]::WriteAllText($tmp, (($script -replace "`r", '') + "`n"), (New-Object Text.UTF8Encoding($false)))
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $wslTmp = (& wsl.exe @wslArgs -- wslpath -a ($tmp -replace '\\', '/') | Out-String).Trim()
        $script:wslLog = New-Object System.Collections.Generic.List[string]
        & wsl.exe @wslArgs @extra -- bash $wslTmp 2>&1 | ForEach-Object { $line = "$_"; $script:wslLog.Add($line); Write-Host $line }
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prev
        Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
    }
}

function ConvertTo-WslPath([string]$p) {
    $r = (& wsl.exe @wslArgs -- wslpath -a ($p -replace '\\', '/') | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $r) { throw "wslpath non riuscito per $p" }
    return $r
}

# Strumenti in WSL.
$check = 'for t in dpkg-deb rpmbuild mksquashfs curl; do command -v $t >/dev/null || exit 1; done'
if ((Invoke-Wsl @() $check) -ne 0) {
    Write-Host 'Installo in WSL dpkg, rpm, squashfs-tools e curl...' -ForegroundColor Cyan
    $install = 'export DEBIAN_FRONTEND=noninteractive; apt-get update -qq && apt-get install -y -qq dpkg-dev rpm squashfs-tools curl >/dev/null'
    if ((Invoke-Wsl @('-u', 'root') $install) -ne 0) { throw 'Installazione degli strumenti in WSL non riuscita' }
    if ((Invoke-Wsl @() $check) -ne 0) { throw 'Strumenti ancora mancanti in WSL' }
}

# Pubblicazione self-contained linux-x64.
# Ogni esecuzione ha la sua cartella di pubblicazione (build parallele, per esempio le prove Linux del cancello di unione,
# non si svuotano a vicenda i file) e la toglie alla fine. Le cartelle obj\ dei progetti invece sono condivise:
# dotnet publish si serializza con un mutex di sistema (con nome, per cartella del progetto), l'attesa è bloccante.
$publish = Join-Path $root ('artifacts\publish-linux-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$mutexKey = [BitConverter]::ToString([Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))).Replace('-', '')
$mutex = New-Object System.Threading.Mutex($false, "Global\RdpManagerBuildLinux-$mutexKey")
$locked = $false
try {
    try { $locked = $mutex.WaitOne() } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    dotnet publish (Join-Path $root 'src\RdpManager.Linux\RdpManager.Linux.csproj') -c Release -r linux-x64 --self-contained -p:PublishSingleFile=false -p:DebugType=none -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (linux-x64) non riuscito ($LASTEXITCODE)" }
    if (-not (Test-Path (Join-Path $publish 'RdpManager.Linux'))) { throw "Manca RdpManager.Linux in $publish" }
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

$dist = Join-Path $root 'dist'
$tools = Join-Path $root 'artifacts\tools'
New-Item -ItemType Directory -Force $dist, $tools | Out-Null

$script = ConvertTo-WslPath (Join-Path $root 'packaging\linux\build-packages.sh')
$wslPublish = ConvertTo-WslPath $publish
$wslDist = ConvertTo-WslPath $dist
$wslTools = ConvertTo-WslPath $tools
# Copio i file di pacchetto in /tmp di WSL (toglie anche eventuali CR da fine riga Windows) e lancio lo script da lì.
$src = $script.Substring(0, $script.LastIndexOf('/'))
# Cartella unica per esecuzione: build parallele (altre copie del progetto) non si sovrascrivono lo script.
$run = "set -e; D=`$(mktemp -d /tmp/rdpmanager-pkg.XXXXXX); trap 'rm -rf `$D' EXIT; tr -d '\r' < '$script' > `$D/build-packages.sh; " +
       "cp -f '$src/rdpmanager.desktop' '$src/rdpmanager.png' '$src/rdpmanager-rdpmconfig.xml' `$D/; " +
       "bash `$D/build-packages.sh '$wslPublish' '$Version' '$wslDist' '$wslTools'"
$rc = Invoke-Wsl @() $run
Remove-Item -LiteralPath $publish -Recurse -Force -ErrorAction SilentlyContinue
if ($rc -ne 0) {
    # Il log completo della costruzione resta su disco (artifacts\build-linux.log) e la coda finisce nel messaggio d'errore:
    # chi vede solo l'eccezione (cancello di unione, CI) trova subito la riga vera.
    $logFile = Join-Path $root 'artifacts\build-linux.log'
    $all = @($script:wslLog)
    try { [IO.File]::WriteAllLines($logFile, [string[]]$all, (New-Object Text.UTF8Encoding($false))) } catch { }
    $tail = ($all | Select-Object -Last 40) -join "`n"
    throw "Costruzione dei pacchetti Linux non riuscita (codice $rc). Log completo: $logFile`n--- ultime righe ---`n$tail"
}

$files = @(
    "rdpmanager_${Version}_amd64.deb",
    "rdpmanager-${Version}-1.x86_64.rpm",
    "RdpManager-${Version}-x86_64.AppImage"
) | ForEach-Object { Join-Path $dist $_ }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "Pacchetto mancante: $f" } }
$files
