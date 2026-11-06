<#
.SYNOPSIS
  Crea il file di configurazione del client (rdpmanager-config.json) dai dati di config/locale.json.
.DESCRIPTION
  Scrive server, codice di registrazione ed endpoint VPN nella cartella indicata (predefinita: artifacts/config-client,
  ignorata da git). Il file contiene il codice di registrazione: dallo a chi deve registrarsi, non pubblicarlo.
  Chi lo riceve lo mette accanto a RdpManager.exe o in %APPDATA%\RdpManager, oppure lo importa dal dialogo
  dell'account con «Importa configurazione…».
.EXAMPLE
  pwsh server/crea-config-client.ps1
.EXAMPLE
  pwsh server/crea-config-client.ps1 -Cartella D:\consegna
#>
param(
    [string]$Cartella = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'config-locale.ps1')
$cfg = Get-ConfigLocale
if (-not $cfg.CodiceRegistrazione) {
    throw "config/locale.json: manca 'codiceRegistrazione' (o e' ancora il segnaposto). Mettilo, rilancia il deploy e poi questo script."
}
if (-not $Cartella) { $Cartella = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/config-client' }
New-Item -ItemType Directory -Force $Cartella | Out-Null
$config = [ordered]@{
    server              = $cfg.UrlServer
    codiceRegistrazione = $cfg.CodiceRegistrazione
    vpnEndpoint         = "$($cfg.Dominio):$($cfg.PortaVpn)"
}
$file = Join-Path $Cartella 'rdpmanager-config.json'
$config | ConvertTo-Json | Set-Content -Path $file -Encoding UTF8
Write-Host "Scritto $file"
