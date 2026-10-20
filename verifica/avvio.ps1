# Criterio: l'app si avvia e resta aperta senza eccezioni (finestra principale con elenco connessioni).
# Uso (funziona da bash, cmd e PowerShell):
#   powershell -NoProfile -ExecutionPolicy Bypass -File <cartella del progetto>/verifica/avvio.ps1
#
# Compila in cartelle proprie (obj/verifica-avvio, bin/verifica-avvio) e non in obj/Release: così
# può girare insieme a «dotnet build»/«dotnet test» della soluzione senza contendersi gli stessi
# file (errore CS2012 «file usato da un altro processo»).
$project = Join-Path $PSScriptRoot '..\src\RdpManager\RdpManager.csproj'
$out = Join-Path $PSScriptRoot '..\src\RdpManager\bin\verifica-avvio\'
dotnet build $project -c Release -nologo -v q '-p:IntermediateOutputPath=obj\verifica-avvio\' "-p:OutputPath=$out"
if ($LASTEXITCODE -ne 0) { exit 1 }
$p = Start-Process (Join-Path $out 'RdpManager.exe') -PassThru
Start-Sleep 20
$p.Refresh()
if ($p.HasExited) { exit 1 }
Stop-Process -Id $p.Id
'OK'
