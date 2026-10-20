# Criterio: README.md in italiano con le istruzioni (compreso dotnet publish).
# Uso (funziona da bash, cmd e PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <cartella del progetto>/verifica/readme.ps1
$readme = Join-Path $PSScriptRoot '..\README.md'
if ((Test-Path $readme) -and ((Get-Content -Raw $readme) -match 'dotnet publish')) { 'OK' } else { exit 1 }
