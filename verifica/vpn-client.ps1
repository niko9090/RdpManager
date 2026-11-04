# Criterio: l'installer del client VPN si genera dal profilo di esempio e il file non e' vuoto.
# Uso (funziona da bash, cmd e PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <cartella del progetto>/verifica/vpn-client.ps1
# Non esegue l'installer generato.
$radice = Split-Path $PSScriptRoot -Parent
Push-Location $radice
try {
    powershell -NoProfile -File build-vpn-client.ps1 -Profilo tests/fixtures/vpn-esempio.conf -Nome prova | Out-Null
    if ($LASTEXITCODE -ne 0) { exit 1 }
    $exe = Join-Path $radice 'dist/RdpManagerVPN-Setup-prova.exe'
    if ((Test-Path $exe) -and ((Get-Item $exe).Length -gt 0)) { 'OK' } else { exit 1 }
} finally { Pop-Location }
