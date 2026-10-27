param([Parameter(Mandatory)][ValidateSet('riuscito','sha','errore','simula')][string]$Caso)
# Prova del programma di aggiornamento (dalla 1.6.1) sull'installazione vera, con l'app aperta e le sessioni ripristinate.
# Uso: verifica/aggiornamento.ps1 -Caso riuscito|sha|errore|simula  (RdpManager installato, con sessioni da riaprire salvate).
# riuscito = reinstalla l'ultimo Setup in dist; sha = SHA256 sbagliato (non deve partire); errore = installer che esce con 1;
# simula = installa e finge che la verifica della versione fallisca. Esito in artifacts\prova-aggiornamento\<caso>\esito.json.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
$root = Split-Path $PSScriptRoot -Parent
$d = Join-Path $root 'artifacts\prova-aggiornamento'
$inst = "$env:LOCALAPPDATA\Programs\RdpManager"
$exe = "$inst\RdpManager.exe"
$riserva = "$env:LOCALAPPDATA\RdpManager\update-runner\riserva"
$dati = "$env:APPDATA\RdpManager\connections.json"
$setup = (Get-ChildItem (Join-Path $root 'dist') -Filter 'RdpManager-Setup-*.exe' | Sort-Object { [version]($_.BaseName -replace '^RdpManager-Setup-', '') } | Select-Object -Last 1).FullName
$out = "$d\$Caso"
New-Item -ItemType Directory -Force $out | Out-Null

function Shot($n) { $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height; $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size); $bmp.Save("$out\$n.png"); $g.Dispose(); $bmp.Dispose() }
function InstHash { (Get-ChildItem $inst -Recurse -File | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName).Hash + ' ' + $_.FullName.Substring($inst.Length) }) -join "`n" | ForEach-Object { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($_))).Replace('-', '') } }
function Tabs($p) {
    $w = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    ($w.FindAll('Descendants', $c) | ForEach-Object { $_.Current.Name.Trim() }) -join ', '
}
function WaitApp { for ($i = 0; $i -lt 60; $i++) { $p = Get-Process RdpManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe -and $_.MainWindowHandle -ne 0 }; if ($p) { return $p | Select-Object -First 1 }; Start-Sleep 1 }; return $null }

# 1) app aperta (riapre da sola le 2 sessioni salvate)
$app = Get-Process RdpManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Select-Object -First 1
if (-not $app) { Start-Process $exe | Out-Null; Start-Sleep 6; $app = WaitApp }
Start-Sleep 3
$r = [ordered]@{ caso = $Caso; inizio = (Get-Date -f s) }
$r.versionePrima = (Get-Item $exe).VersionInfo.FileVersion
$r.schedePrima = Tabs $app
$r.datiPrima = (Get-FileHash $dati).Hash
$r.cartellaPrima = InstHash
Shot '1-prima'

# 2) copia di riserva, come PrepareUpdateRunner
if (Test-Path $riserva) { Remove-Item -Recurse -Force -LiteralPath $riserva }
robocopy $inst $riserva /MIR /NFL /NDL /NJH /NJS /NP | Out-Null

# 3) installer e SHA256 per il caso
$installer = "$out\installer.exe"
switch ($Caso) {
    'errore' { Copy-Item "$env:WINDIR\System32\whoami.exe" $installer -Force }   # termina con codice 1 sugli argomenti di Inno
    default  { Copy-Item $setup $installer -Force }
}
$sha = (Get-FileHash $installer).Hash.ToLowerInvariant()
if ($Caso -eq 'sha') { $sha = ('0' * 64) }
$prec = [version]((Get-Item $exe).VersionInfo.FileVersion)
$precedente = '{0}.{1}.{2}' -f $prec.Major, $prec.Minor, $prec.Build
$setupVer = (Get-Item $setup).VersionInfo.ProductVersion
# riuscito/simula: la versione del Setup; sha/errore: una versione più nuova inventata (l'installer non la porta)
$versione = if ($Caso -eq 'riuscito' -or $Caso -eq 'simula') { $setupVer } else { '{0}.{1}.{2}' -f $prec.Major, $prec.Minor, ($prec.Build + 1) }
$log = "$out\aggiornamento.log"; $logSetup = "$out\setup.log"
Remove-Item $log, $logSetup -ErrorAction SilentlyContinue
$argv = @('--aggiorna', '--pid', $app.Id, '--installer', $installer, '--sha256', $sha, '--cartella', $inst, '--riserva', $riserva,
          '--versione', $versione, '--precedente', $precedente, '--log', $log, '--log-setup', $logSetup)
if ($Caso -eq 'simula') { $argv += '--simula-errore' }
$runner = Start-Process "$riserva\RdpManager.exe" -ArgumentList ($argv | ForEach-Object { '"' + $_ + '"' }) -WorkingDirectory $riserva -PassThru
Start-Sleep 2
# 4) l'app esce (le sessioni sono ferme sulla richiesta della password, che è modale: la chiusura si fa terminando il processo)
Stop-Process -Id $app.Id -Force
$null = $runner.WaitForExit(600000)
$r.runnerFinito = $runner.HasExited
Start-Sleep 4
$new = WaitApp
Start-Sleep 4
Shot '2-dopo'
$r.versioneDopo = (Get-Item $exe).VersionInfo.FileVersion
$r.riaperta = [bool]$new
if ($new) { $r.titolo = $new.MainWindowTitle; $r.schedeDopo = Tabs $new }
$msg = [System.Windows.Automation.AutomationElement]::RootElement.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Aggiornamento non riuscito')))
$r.messaggioFallimento = $msg.Count -gt 0
$r.datiDopo = (Get-FileHash $dati).Hash
$r.datiInvariati = $r.datiPrima -eq $r.datiDopo
$r.cartellaDopo = InstHash
$r.cartellaInvariata = $r.cartellaPrima -eq $r.cartellaDopo
$r.logSetupEsiste = Test-Path $logSetup
$r.logRunner = if (Test-Path $log) { (Get-Content $log) -join ' | ' } else { '(manca)' }
$r | ConvertTo-Json | Set-Content "$out\esito.json" -Encoding UTF8
$r | ConvertTo-Json


