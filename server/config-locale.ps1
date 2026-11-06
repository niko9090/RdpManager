# Legge la configurazione locale (config/locale.json, NON pubblicata) per gli script di deploy e di verifica.
# Uso:  . "$PSScriptRoot/config-locale.ps1"; $cfg = Get-ConfigLocale
# Il modello da copiare e' config/locale.example.json.
function Get-ConfigLocale {
    $radice = Split-Path -Parent $PSScriptRoot
    $file = Join-Path $radice 'config/locale.json'
    if (-not (Test-Path $file)) {
        throw "Manca $file. Copia config/locale.example.json in config/locale.json e metti i tuoi valori."
    }
    $j = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
    function Valore($nome, $predefinito) {
        $v = $j.PSObject.Properties[$nome]
        if ($v -and "$($v.Value)".Length -gt 0) { return $v.Value }
        if ($null -ne $predefinito) { return $predefinito }
        throw "config/locale.json: manca il valore '$nome' (vedi config/locale.example.json)."
    }
    $dominio = [string](Valore 'dominio' $null)
    if ($dominio -like '*example.com' -or $dominio -like '*example.org') {
        throw "config/locale.json contiene ancora il dominio di esempio ($dominio): scrivi il tuo."
    }
    $portaHttps = [int](Valore 'portaHttps' 8443)
    [pscustomobject]@{
        Dominio     = $dominio
        PortaHttps  = $portaHttps
        PortaVpn    = [int](Valore 'portaVpn' 51820)
        SshAlias    = [string](Valore 'sshAlias' $null)
        SshPorta    = [int](Valore 'sshPorta' 22)
        CartellaNas = [string](Valore 'cartellaNas' '/volume1/docker')
        ReteLan     = [string]$(if ($j.PSObject.Properties['reteLan']) { $j.reteLan } else { '' })
        UrlServer   = "https://${dominio}:$portaHttps"
    }
}
