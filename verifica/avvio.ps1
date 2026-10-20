# Criterio: l'app si avvia e resta aperta senza eccezioni (finestra principale con elenco connessioni).
# Uso (funziona da bash, cmd e PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <cartella del progetto>/verifica/avvio.ps1
$project = Join-Path $PSScriptRoot '..\src\RdpManager'
$p = Start-Process dotnet -ArgumentList 'run', '--project', $project, '-c', 'Release' -PassThru
Start-Sleep 20
if ($p.HasExited -and $p.ExitCode -ne 0) { exit 1 }
Get-Process RdpManager -ErrorAction Stop | Stop-Process
'OK'
