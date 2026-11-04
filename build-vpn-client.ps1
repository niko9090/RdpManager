# Pubblica RdpManagerVpn (Release, win-x64, self-contained) e compila con Inno Setup l'installer di un PC client:
#   dist\RdpManagerVPN-Setup-<Nome>.exe   contiene già il profilo WireGuard del PC (nessun dato da inserire)
#
# Uso:   powershell -NoProfile -File build-vpn-client.ps1 -Profilo <file.conf> -Nome <pc>
# Il profilo si controlla (campi obbligatori) prima di compilare. wireguard.dll viaggia accanto all'exe
# (la copia RdpManager.csproj nella pubblicazione, come per RdpManager).

param(
    [Parameter(Mandatory = $true)][string]$Profilo,
    [Parameter(Mandatory = $true)][string]$Nome
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\RdpManagerVpn\RdpManagerVpn.csproj'

if ($Nome -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw "Nome non valido ('$Nome'): usare lettere, cifre, punto, trattino e trattino basso (finisce nel nome del file)."
}
if (-not (Test-Path -LiteralPath $Profilo -PathType Leaf)) { throw "Profilo non trovato: $Profilo" }
$profilePath = (Resolve-Path -LiteralPath $Profilo).Path
$text = Get-Content -Raw -LiteralPath $profilePath
foreach ($campo in 'PrivateKey', 'Address', 'PublicKey', 'Endpoint') {
    if ($text -notmatch "(?mi)^\s*$campo\s*=\s*\S") { throw "Nel profilo manca il campo $campo ($profilePath)" }
}

[xml]$csproj = Get-Content -Raw $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "Versione non trovata in $project" }
$version = $version.Trim()
Write-Host "RdpManager VPN $version per '$Nome'"

$publishDir = Join-Path $root 'artifacts\publish-vpn'
$dist = Join-Path $root 'dist'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish non riuscito ($LASTEXITCODE)" }
foreach ($f in 'RdpManagerVpn.exe', 'wireguard.dll') {
    if (-not (Test-Path (Join-Path $publishDir $f))) { throw "Manca $f in $publishDir" }
}
# Il profilo entra solo nell'installer, mai nella pubblicazione.
Remove-Item -ErrorAction SilentlyContinue (Join-Path $publishDir 'vpn.conf')

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) non trovato' }

& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DProfilePath=$profilePath" "/DClientName=$Nome" "/O$dist" (Join-Path $root 'installer\RdpManagerVpn.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC non riuscito ($LASTEXITCODE)" }

$setup = Join-Path $dist "RdpManagerVPN-Setup-$Nome.exe"
if (-not (Test-Path $setup)) { throw "Installer non trovato: $setup" }
Get-Item $setup | ForEach-Object { '{0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
