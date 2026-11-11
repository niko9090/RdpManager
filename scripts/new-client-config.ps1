<#
.SYNOPSIS
  Crea il file di configurazione dei client di RdpManager (.rdpmconfig) con i valori veri del tuo server.
.DESCRIPTION
  Legge dominio, porte, alias SSH e codice di registrazione da config/locale.json (come gli script di deploy) e, via ssh,
  la chiave pubblica del server WireGuard (server.pub) dal Synology. Se in config/locale.json manca il codice di
  registrazione lo legge dal .env di rdpm-sync sul NAS. Scrive un unico file .rdpmconfig da consegnare al PC (Windows o
  Linux): chi lo riceve lo carica con «Carica file di configurazione…» o con un doppio clic e non compila niente.
  Il file contiene segreti (codice di registrazione, e la password se la indichi): consegnalo in privato, non pubblicarlo.
  Con -ChiavePubblicaServer si evita l'accesso ssh (per esempio se il NAS non è raggiungibile).
.PARAMETER Cartella
  Dove scrivere il file (predefinita: artifacts/config-client, ignorata da git).
.PARAMETER Nome
  Nome del file senza estensione (predefinito: RdpManager).
.PARAMETER Utente
  Facoltativo: account da registrare/usare sul PC. Con -Password il PC si registra e accede da solo.
.PARAMETER Password
  Password dell'account (solo insieme a -Utente). Finisce in chiaro nel file.
.PARAMETER ChiavePubblicaServer
  Chiave pubblica WireGuard del server, se non la si vuole leggere dal NAS.
.PARAMETER UrlInterno
  Indirizzo del server dentro la VPN (predefinito http://10.66.0.1:8787).
.PARAMETER SenzaChiaveAlServer
  Non chiedere al server la registrazione automatica del PC (il file avrà richiediChiaveAlServer = false).
.EXAMPLE
  pwsh scripts/new-client-config.ps1
.EXAMPLE
  pwsh scripts/new-client-config.ps1 -Cartella D:\consegna -Nome Portatile-Anna
#>
param(
    [string]$Cartella = '',
    [string]$Nome = 'RdpManager',
    [string]$Utente = '',
    [string]$Password = '',
    [string]$ChiavePubblicaServer = '',
    [string]$UrlInterno = 'http://10.66.0.1:8787',
    [switch]$SenzaChiaveAlServer
)
$ErrorActionPreference = 'Stop'
$radice = Split-Path $PSScriptRoot -Parent
. (Join-Path $radice 'server/config-locale.ps1')
$cfg = Get-ConfigLocale

if ([bool]$Utente -ne [bool]$Password) { throw 'Servono insieme -Utente e -Password (o nessuno dei due).' }
if ($Nome -notmatch '^[A-Za-z0-9._ -]+$') { throw "Nome del file non valido: '$Nome' (lettere, cifre, spazio e . _ -)." }

function Invoke-Nas([string]$comando) {
    $sshArgs = @('-o', 'BatchMode=yes')
    if ($cfg.SshPorta -ne 22) { $sshArgs += @('-p', "$($cfg.SshPorta)") }
    $out = & ssh @sshArgs $cfg.SshAlias $comando
    if ($LASTEXITCODE -ne 0) { throw "ssh verso '$($cfg.SshAlias)' non riuscito: $comando" }
    return ($out -join "`n").Trim()
}

# Chiave pubblica del server WireGuard
$chiave = $ChiavePubblicaServer.Trim()
if (-not $chiave) {
    Write-Host "Leggo la chiave pubblica del server da $($cfg.SshAlias)..."
    $chiave = Invoke-Nas "sudo -n cat '$($cfg.CartellaNas)/wireguard/config/server.pub'"
}
if ($chiave -notmatch '^[A-Za-z0-9+/]{43}=$') { throw "La chiave pubblica del server non e' valida: '$chiave'." }

# Codice di registrazione: config/locale.json, altrimenti il .env di rdpm-sync sul NAS
$codice = $cfg.CodiceRegistrazione
if (-not $codice -and -not $ChiavePubblicaServer) {
    Write-Host 'Nessun codice in config/locale.json: lo leggo dal .env sul NAS...'
    $riga = Invoke-Nas "sudo -n grep '^RDPM_SYNC_REGISTRATION_CODE=' '$($cfg.CartellaNas)/rdpm-sync/.env'"
    $codice = ($riga -replace '^RDPM_SYNC_REGISTRATION_CODE=', '').Trim().Trim('"', "'")
}
if (-not $codice) { Write-Warning 'Nessun codice di registrazione: il file funzionera'' solo se il server ha la registrazione aperta.' }

$rete = '10.66.0.0/24'
$server = [ordered]@{ url = $cfg.UrlServer; urlInterno = $UrlInterno }
if ($codice) { $server['codiceRegistrazione'] = $codice }
$vpn = [ordered]@{
    endpoint               = "$($cfg.Dominio):$($cfg.PortaVpn)"
    chiavePubblicaServer   = $chiave
    rete                   = $rete
    allowedIPs             = $rete
    keepalive              = 25
    richiediChiaveAlServer = (-not $SenzaChiaveAlServer)
}
$file = [ordered]@{
    _commento = 'File di configurazione di RdpManager: caricalo con «Carica file di configurazione…» o con un doppio clic. Contiene segreti: non pubblicarlo.'
    formato   = 'rdpmconfig'
    versione  = 1
    server    = $server
}
if ($Utente) { $file['account'] = [ordered]@{ utente = $Utente; password = $Password } }
$file['vpn'] = $vpn

if (-not $Cartella) { $Cartella = Join-Path $radice 'artifacts/config-client' }
New-Item -ItemType Directory -Force $Cartella | Out-Null
$percorso = Join-Path $Cartella "$Nome.rdpmconfig"
[IO.File]::WriteAllText($percorso, (($file | ConvertTo-Json -Depth 5) + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "Scritto $percorso"
Write-Host 'Consegnalo in privato (contiene il codice di registrazione): su Windows doppio clic, su Linux "rdpmanager file.rdpmconfig" o Strumenti > Carica file di configurazione.'
