<#
.SYNOPSIS
  Mette in opera (o aggiorna) sul Synology wireguard + rdpm-sync + proxy HTTPS. Idempotente.
.DESCRIPTION
  Copia server/wireguard-synology, server/rdpm-sync (senza bin/obj) e server/caddy-synology sul NAS,
  installa Docker se manca, genera le chiavi WireGuard e il .env (chiave admin e codice di registrazione
  casuali) solo se mancano, avvia i contenitori con restart unless-stopped e verifica /health e "wg show".
  Legge dominio, alias SSH e rete LAN da config/locale.json (modello: config/locale.example.json, non pubblicato).
  Richiede l'alias SSH indicato li' (utente con sudo senza password). Non scrive segreti nel repo.
.EXAMPLE
  pwsh server/deploy-synology.ps1
#>
param(
    [string]$Alias = '',
    [string]$PublicHost = ''
)
$ErrorActionPreference = 'Stop'
$srv = $PSScriptRoot
. (Join-Path $srv 'config-locale.ps1')
$cfg = Get-ConfigLocale
if (-not $Alias) { $Alias = $cfg.SshAlias }
if (-not $PublicHost) { $PublicHost = $cfg.Dominio }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("rdpm-deploy-" + [guid]::NewGuid().ToString('N'))
$tar = "$tmp.tar"
New-Item -ItemType Directory $tmp | Out-Null
try {
    foreach ($d in 'wireguard-synology', 'rdpm-sync', 'caddy-synology') {
        Copy-Item (Join-Path $srv $d) (Join-Path $tmp $d) -Recurse
        foreach ($x in 'bin', 'obj', 'data') {
            $p = Join-Path (Join-Path $tmp $d) $x
            if (Test-Path $p) { Remove-Item $p -Recurse -Force }
        }
    }
    # wireguard-synology/config: solo il modello (le chiavi vere restano sul NAS)
    Get-ChildItem (Join-Path $tmp 'wireguard-synology/config') -File -Force |
        Where-Object Name -ne 'wg0.conf.modello' | Remove-Item -Force
    tar -cf $tar -C $tmp .
    if ($LASTEXITCODE) { throw 'tar fallito' }

    ssh -o BatchMode=yes $Alias 'rm -rf /tmp/rdpm-deploy && mkdir -p /tmp/rdpm-deploy'
    scp -O -o BatchMode=yes $tar "${Alias}:/tmp/rdpm-deploy.tar"
    if ($LASTEXITCODE) { throw 'scp fallito' }
    scp -O -o BatchMode=yes (Join-Path $srv 'deploy-synology.remote.sh') "${Alias}:/tmp/rdpm-deploy.sh"
    if ($LASTEXITCODE) { throw 'scp fallito' }
    ssh -o BatchMode=yes $Alias 'tar -xf /tmp/rdpm-deploy.tar -C /tmp/rdpm-deploy && rm /tmp/rdpm-deploy.tar'
    $remote = "sudo -n env DEPLOY_HOST=$PublicHost DEPLOY_LAN=$($cfg.ReteLan) DEPLOY_BASE=$($cfg.CartellaNas) DEPLOY_PORT_VPN=$($cfg.PortaVpn) bash /tmp/rdpm-deploy.sh; rc=`$?; rm -f /tmp/rdpm-deploy.sh; exit `$rc"
    ssh -o BatchMode=yes $Alias $remote
    if ($LASTEXITCODE) { throw 'deploy fallito sul Synology' }
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $tar -Force -ErrorAction SilentlyContinue
}
