# Pubblica RdpManager (self-contained, win-x64) e compila l'installer Inno Setup.
# L'installer contiene la pubblicazione a cartella (RdpManager.exe con le sue librerie, nessun exe unico
# compresso: è meno sospetta per gli antivirus). La versione portatile resta un exe unico, non compresso.
# Risultato in dist\:
#   RdpManager-Setup-<versione>.exe          installer per utente (senza diritti di amministratore)
#   RdpManager-<versione>-win-x64.exe        eseguibile portatile
#   SHA256SUMS.txt                           SHA256 dei due file (formato sha256sum): l'aggiornamento con un
#                                            clic dell'app lo usa per verificare l'installer prima di avviarlo
#
# Uso:   powershell -NoProfile -ExecutionPolicy Bypass -File build-installer.ps1
# La versione si legge da <Version> in src\RdpManager\RdpManager.csproj.
# -ExpectedVersion (usato dal CI con il nome del tag) fa fallire lo script se non coincide.

param(
    [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\RdpManager\RdpManager.csproj'

[xml]$csproj = Get-Content -Raw $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "Versione non trovata in $project" }
$version = $version.Trim()
if ($ExpectedVersion -and ($ExpectedVersion.TrimStart('v') -ne $version)) {
    throw "Il tag ($ExpectedVersion) non coincide con la versione del progetto ($version): aggiornare <Version> nel csproj."
}
Write-Host "RdpManager $version"

$publishDir = Join-Path $root 'artifacts\publish'
$portableDir = Join-Path $root 'artifacts\portable'
$dist = Join-Path $root 'dist'
foreach ($d in $publishDir, $portableDir) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
New-Item -ItemType Directory -Force $dist | Out-Null

# Per l'installer: a cartella.
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (cartella) non riuscito ($LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $publishDir 'RdpManager.exe'))) { throw "Manca RdpManager.exe in $publishDir" }

# Versione portatile: exe unico senza compressione.
dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o $portableDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (portatile) non riuscito ($LASTEXITCODE)" }
$exe = Join-Path $portableDir 'RdpManager.exe'
if (-not (Test-Path $exe)) { throw "Manca $exe dopo la pubblicazione" }

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) non trovato' }

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" "/O$dist" (Join-Path $root 'installer\RdpManager.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC non riuscito ($LASTEXITCODE)" }

$portable = Join-Path $dist "RdpManager-$version-win-x64.exe"
Copy-Item $exe $portable -Force

$setup = Join-Path $dist "RdpManager-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "Installer non trovato: $setup" }

# SHA256SUMS.txt: "<hash>  <nome>" con a capo LF, UTF-8 senza BOM (come sha256sum).
$sums = Join-Path $dist 'SHA256SUMS.txt'
$lines = foreach ($f in $setup, $portable) {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLowerInvariant(), (Split-Path $f -Leaf)
}
[IO.File]::WriteAllText($sums, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

Get-Item $setup, $portable, $sums | ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
