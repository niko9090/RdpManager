# Verifica end-to-end: registrazione utente di prova, login, POST /v1/vpn/devices, peer in WireGuard sul NAS, idempotenza, pulizia.
# Uso (Windows PowerShell 5.1 o PowerShell 7, da qualunque cartella):
#   powershell -NoProfile -File <cartella del progetto>/verifica/vpn-registrazione-e2e.ps1
# Esce 0 se tutti i controlli passano, 1 altrimenti. Legge dal NAS (via ssh) il codice di registrazione e la chiave admin:
# non li stampa e non li scrive nel repo. Compatibile con Windows PowerShell 5.1 (niente HKDF/Pbkdf2 SHA-256 nativi: li fa un
# piccolo helper C# con HMAC-SHA256, verificato sul vettore di prova di docs/sync-server-api.md, sezione 2).

$ErrorActionPreference = 'Stop'
$script:base = 'https://syncnetpro.com:8443'
$script:sshAlias = 'synology'
$script:sshPort = 26
$script:docker = 'sudo -n env PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin docker'
$script:envFile = '/volume1/docker/rdpm-sync/.env'
$script:fallimenti = 0

try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

Add-Type -AssemblyName System.Net.Http
Add-Type -TypeDefinition @'
using System;
using System.Security.Cryptography;
public static class RdpmE2eCrypto {
    static byte[] Hmac(byte[] key, byte[] data) { using (var h = new HMACSHA256(key)) return h.ComputeHash(data); }
    // PBKDF2-HMAC-SHA256 con output di 32 byte (un solo blocco).
    public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations) {
        using (var h = new HMACSHA256(password)) {
            var blk = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, blk, 0, salt.Length);
            blk[blk.Length - 1] = 1;
            var u = h.ComputeHash(blk);
            var t = (byte[])u.Clone();
            for (int i = 1; i < iterations; i++) {
                u = h.ComputeHash(u);
                for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
            }
            return t;
        }
    }
    // HKDF-SHA256 con salt vuoto e output di 32 byte.
    public static byte[] HkdfSha256Empty(byte[] ikm, byte[] info) {
        var prk = Hmac(new byte[32], ikm);
        var data = new byte[info.Length + 1];
        Buffer.BlockCopy(info, 0, data, 0, info.Length);
        data[info.Length] = 1;
        return Hmac(prk, data);
    }
}
'@

$script:client = New-Object System.Net.Http.HttpClient
$script:client.Timeout = [TimeSpan]::FromSeconds(60)

function Verifica([string]$nome, [bool]$ok, [string]$dettaglio = '') {
    if ($ok) { Write-Host "[OK]   $nome" }
    else { $script:fallimenti++; Write-Host "[FAIL] $nome $dettaglio" }
}

# Esegue un comando sul NAS. Restituisce le righe di output; lancia eccezione se ssh esce con errore.
function Ssh-Riga([string]$comando) {
    $ErrorActionPreference = 'Continue'
    $out = & ssh -p $script:sshPort -o BatchMode=yes -o ConnectTimeout=20 $script:sshAlias $comando 2>$null
    if ($LASTEXITCODE -ne 0) { throw "ssh fallito (codice $LASTEXITCODE)" }
    return @($out | ForEach-Object { "$_".TrimEnd() })
}

function Valore-Env([string[]]$righe, [string]$nome) {
    $riga = $righe | Where-Object { $_ -match ('^\s*' + [regex]::Escape($nome) + '\s*=') } | Select-Object -First 1
    if (-not $riga) { return '' }
    $v = $riga.Substring($riga.IndexOf('=') + 1).Trim()
    if ($v.Length -ge 2 -and (($v[0] -eq '"' -and $v[-1] -eq '"') -or ($v[0] -eq "'" -and $v[-1] -eq "'"))) {
        $v = $v.Substring(1, $v.Length - 2)
    }
    return $v
}

function Peer-NAS {
    return @(Ssh-Riga "$($script:docker) exec rdpm-sync wg show wg0 peers" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Invoca([string]$metodo, [string]$percorso, $corpo = $null, [string]$token = '', [string]$chiaveAdmin = '') {
    $m = New-Object System.Net.Http.HttpMethod -ArgumentList $metodo
    $req = New-Object System.Net.Http.HttpRequestMessage -ArgumentList $m, ([Uri]($script:base + $percorso))
    if ($token) { $req.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue -ArgumentList 'Bearer', $token }
    if ($chiaveAdmin) { [void]$req.Headers.TryAddWithoutValidation('X-Admin-Key', $chiaveAdmin) }
    if ($null -ne $corpo) {
        $json = ($corpo | ConvertTo-Json -Compress)
        $req.Content = New-Object System.Net.Http.StringContent -ArgumentList $json, ([Text.Encoding]::UTF8), 'application/json'
    }
    $resp = $script:client.SendAsync($req).GetAwaiter().GetResult()
    $testo = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $dati = $null
    if ($testo) { try { $dati = $testo | ConvertFrom-Json } catch { $dati = $null } }
    return [pscustomobject]@{ Stato = [int]$resp.StatusCode; Dati = $dati }
}

# Segreto di accesso come da docs/sync-server-api.md, sezione 2 (punti 1-3).
function Segreto-Accesso([string]$utente, [string]$password) {
    $u = $utente.Trim().ToLowerInvariant()
    $sale = [System.Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('rdpm-sync/v1/salt/' + $u))
    $master = [RdpmE2eCrypto]::Pbkdf2Sha256([Text.Encoding]::UTF8.GetBytes($password), $sale, 600000)
    return [Convert]::ToBase64String([RdpmE2eCrypto]::HkdfSha256Empty($master, [Text.Encoding]::ASCII.GetBytes('rdpm-sync/v1/auth')))
}

# Controllo di sicurezza sul derivatore: il vettore di prova del documento deve coincidere.
$vettoreOk = (Segreto-Accesso 'mario' 'correct horse battery staple') -eq 'LYH1gkWICwAF8nBgnDp+hpjco7z+BEnEW50myOk2GcA='
Verifica 'derivazione segreto di accesso: vettore di prova' $vettoreOk

# Stato della prova, usato dalla pulizia.
$script:utente = $null
$script:nomePc = $null
$script:pub = $null
$script:token = $null
$script:registrato = $false
$script:dispositivoCreato = $false

$codice = ''
$chiaveAdmin = ''
try {
    $righeEnv = Ssh-Riga "sudo -n cat $($script:envFile)"
    $codice = Valore-Env $righeEnv 'RDPM_SYNC_REGISTRATION_CODE'
    $chiaveAdmin = Valore-Env $righeEnv 'RDPM_SYNC_ADMIN_KEY'
    $righeEnv = $null
}
catch { $codice = ''; $chiaveAdmin = '' }
Verifica 'lettura codice di registrazione e chiave admin dal NAS' (($codice.Length -gt 0) -and ($chiaveAdmin.Length -gt 0))

if ($codice.Length -eq 0 -or $chiaveAdmin.Length -eq 0) {
    Write-Host 'ESITO: FALLITO (configurazione non letta, nessun utente creato)'
    exit 1
}

try {
    $salute = Invoca 'GET' '/health'
    Verifica 'server raggiungibile (GET /health 200)' ($salute.Stato -eq 200)

    $script:utente = 'e2e-' + ([guid]::NewGuid().ToString('N')).Substring(0, 10)
    $script:nomePc = 'e2e-pc-' + ([guid]::NewGuid().ToString('N')).Substring(0, 8)
    $passwordProva = ([guid]::NewGuid().ToString('N')) + ([guid]::NewGuid().ToString('N'))
    $segreto = Segreto-Accesso $script:utente $passwordProva

    $reg = Invoca 'POST' '/v1/register' @{ username = $script:utente; password = $segreto; registrationCode = $codice }
    $script:registrato = ($reg.Stato -eq 201)
    Verifica 'registrazione utente di prova (201)' $script:registrato "stato $($reg.Stato)"

    $log = Invoca 'POST' '/v1/login' @{ username = $script:utente; password = $segreto; deviceName = 'e2e-verifica' }
    $script:token = if ($log.Dati) { [string]$log.Dati.token } else { '' }
    Verifica 'login con il segreto di accesso (200 con token)' (($log.Stato -eq 200) -and $script:token.Length -gt 0) "stato $($log.Stato)"

    # Chiave WireGuard di prova: la privata nasce e muore sul NAS, si riporta solo la pubblica.
    $pubRiga = Ssh-Riga "$($script:docker) exec rdpm-sync sh -c 'wg genkey | wg pubkey'"
    $script:pub = [string]($pubRiga | Select-Object -First 1)
    Verifica 'chiave pubblica WireGuard di prova (44 caratteri base64)' ($script:pub -match '^[A-Za-z0-9+/]{43}=$')

    $chiaveServerNAS = [string](Ssh-Riga "$($script:docker) exec rdpm-sync wg show wg0 public-key" | Select-Object -First 1)

    $p1 = Invoca 'POST' '/v1/vpn/devices' @{ name = $script:nomePc; publicKey = $script:pub } $script:token
    Verifica 'POST /v1/vpn/devices (201 nuovo dispositivo)' ($p1.Stato -eq 201) "stato $($p1.Stato)"
    $script:dispositivoCreato = ($p1.Stato -eq 201)
    $d1 = $p1.Dati
    if ($d1) {
        Verifica 'address 10.66.0.N/32' ([string]$d1.address -match '^10\.66\.0\.\d{1,3}/32$') "address=$($d1.address)"
        Verifica 'serverPublicKey uguale alla chiave reale del server WireGuard' (([string]$d1.serverPublicKey -eq $chiaveServerNAS) -and ($chiaveServerNAS.Length -eq 44)) "risposta=$($d1.serverPublicKey)"
        Verifica 'endpoint syncnetpro.com:51820' ([string]$d1.endpoint -eq 'syncnetpro.com:51820') "endpoint=$($d1.endpoint)"
        Verifica 'nome del dispositivo nella risposta' ([string]$d1.name -eq $script:nomePc)
    }
    else { Verifica 'corpo della risposta POST /v1/vpn/devices' $false }

    $peersDopo1 = Peer-NAS
    Verifica 'peer presente in wg show wg0 peers sul NAS' ($peersDopo1 -contains $script:pub)

    $lista = Invoca 'GET' '/v1/vpn/devices' $null $script:token
    $trovato = @($lista.Dati | Where-Object { $_.name -eq $script:nomePc }).Count
    Verifica 'GET /v1/vpn/devices elenca il dispositivo' (($lista.Stato -eq 200) -and ($trovato -eq 1)) "stato $($lista.Stato)"

    # Idempotenza: stessa chiave, stesso indirizzo, nessun peer duplicato.
    $p2 = Invoca 'POST' '/v1/vpn/devices' @{ name = $script:nomePc; publicKey = $script:pub } $script:token
    Verifica 'seconda POST con la stessa chiave (200 gia presente)' ($p2.Stato -eq 200) "stato $($p2.Stato)"
    if ($p2.Dati -and $d1) {
        Verifica 'idempotenza: stesso address' ([string]$p2.Dati.address -eq [string]$d1.address) "prima=$($d1.address) dopo=$($p2.Dati.address)"
    }
    $peersDopo2 = Peer-NAS
    $copie = @($peersDopo2 | Where-Object { $_ -eq $script:pub }).Count
    Verifica 'idempotenza: un solo peer per la chiave sul NAS' ($copie -eq 1) "copie=$copie"
}
catch {
    $script:fallimenti++
    Write-Host ('[FAIL] eccezione durante la prova: ' + $_.Exception.Message)
}
finally {
    # Pulizia sempre: dispositivo, peer su WireGuard, utente di prova.
    try {
        if ($script:token) {
            $del = Invoca 'DELETE' ('/v1/vpn/devices/' + $script:nomePc) $null $script:token
            # Se il dispositivo e' stato creato il DELETE deve dare 204; altrimenti va bene anche 404 (nulla da togliere).
            $delOk = if ($script:dispositivoCreato) { $del.Stato -eq 204 } else { ($del.Stato -eq 204) -or ($del.Stato -eq 404) }
            Verifica 'DELETE /v1/vpn/devices/{nome} (204)' $delOk "stato $($del.Stato)"
        }
        if ($script:pub) {
            $peersFine = Peer-NAS
            Verifica 'peer rimosso da wg show wg0 peers sul NAS' (-not ($peersFine -contains $script:pub))
        }
        if ($script:registrato) {
            $delU = Invoca 'DELETE' ('/v1/admin/accounts/' + $script:utente) $null '' $chiaveAdmin
            Verifica 'eliminazione utente di prova (204)' ($delU.Stato -eq 204) "stato $($delU.Stato)"
            $accounts = Invoca 'GET' '/v1/admin/accounts' $null '' $chiaveAdmin
            $ancora = @($accounts.Dati | Where-Object { $_.username -eq $script:utente }).Count
            Verifica 'utente di prova non piu in elenco admin' (($accounts.Stato -eq 200) -and ($ancora -eq 0))
        }
    }
    catch {
        $script:fallimenti++
        Write-Host ('[FAIL] eccezione in pulizia: ' + $_.Exception.Message)
    }
}

if ($script:fallimenti -gt 0) {
    Write-Host "ESITO: FALLITO ($($script:fallimenti) controlli)"
    exit 1
}
Write-Host 'ESITO: OK'
exit 0
