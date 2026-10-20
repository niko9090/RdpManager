# Criterio: ActiveX RDP in schede (AxHost + TabControl/TabPage), alternativa mstsc, cifratura ProtectedData.
# Uso (funziona da bash, cmd e PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <cartella del progetto>/verifica/codice.ps1
$src = Join-Path $PSScriptRoot '..\src'
$f = Get-ChildItem -Recurse $src -Filter *.cs | Get-Content -Raw
if (($f -match 'AxHost') -and ($f -match 'TabControl|TabPage') -and ($f -match 'mstsc') -and ($f -match 'ProtectedData')) { 'OK' } else { exit 1 }
