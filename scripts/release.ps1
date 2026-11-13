# Pubblica una nuova versione di RdpManager su GitHub.
#
# Uso (dalla cartella del progetto, su un PC con .NET 8 SDK, Inno Setup 6 e gh autenticato):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.1.0
#   (facoltativo) -Notes "Novità di questa versione"; altrimenti la sezione «## X.Y.Z» di CHANGELOG.md
#
# Passi: controlli → <Version> in src\Directory.Build.props → build → test → pubblicazione + installer (build-installer.ps1)
# → commit «Versione X.Y.Z» (se la versione è cambiata) → tag vX.Y.Z → push di main e del tag
# → gh release create con l'installer, l'eseguibile portatile, i pacchetti Linux (.deb, .rpm, AppImage, costruiti in WSL) l'installer VPN generico RdpManagerVPN-Setup.exe (build-vpn-client.ps1) e SHA256SUMS.txt (serve all'aggiornamento con un clic).
# Si ferma senza fare nulla se ci sono modifiche non salvate, se non si è su main o se il tag esiste già.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$repo = 'niko9090/RdpManager'
# Versione unica per Windows, Linux e Core: src\Directory.Build.props (RdpManagerVpn ha la propria nel suo csproj).
$csprojPath = Join-Path $root 'src\Directory.Build.props'

# Note della release: la sezione «## X.Y.Z» di CHANGELOG.md (se c'è) seguita dalle istruzioni di installazione.
function Get-ReleaseNotes([string]$ver) {
    $section = ''
    $changelog = Join-Path $root 'CHANGELOG.md'
    if (Test-Path $changelog) {
        $text = [IO.File]::ReadAllText($changelog, [Text.Encoding]::UTF8)
        $m = [regex]::Match($text, '(?ms)^## ' + [regex]::Escape($ver) + '\s*$(.*?)(?=^## |\z)')
        if ($m.Success) { $section = $m.Groups[1].Value.Trim() + "`n`n" }
    }
    if (-not $section) { $section = "RdpManager $ver.`n`n" }
    return $section +
           "**Installazione:** scarica ed esegui RdpManager-Setup-$ver.exe (per utente, non servono diritti di amministratore; si installa sopra la versione precedente).`n" +
           "RdpManager-$ver-win-x64.exe è la versione portatile, senza installazione.`n" +
           "**Linux (x86_64):** rdpmanager_${ver}_amd64.deb (Ubuntu, Debian, Mint), rdpmanager-$ver-1.x86_64.rpm (Fedora, openSUSE) oppure RdpManager-$ver-x86_64.AppImage (chmod +x e avvio, senza installazione). Serve FreeRDP.`n" +
           "RdpManagerVPN-Setup.exe — da installare sui PC da raggiungere: mettigli accanto il file .rdpmconfig e fai doppio clic.`n" +
           "Le connessioni salvate in %APPDATA%\RdpManager restano anche aggiornando o disinstallando."
}

function Run([string]$what, [scriptblock]$cmd) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what non riuscito (codice $LASTEXITCODE)" }
}

# ---------- controlli ----------
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: «$Version» (atteso X.Y.Z, es. 1.1.0)" }
$tag = "v$Version"

foreach ($tool in 'git', 'gh', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "Manca il comando $tool" }
}
$dirty = git status --porcelain
if ($dirty) { throw "Ci sono modifiche non salvate: fare commit o annullarle prima del rilascio.`n$($dirty -join "`n")" }
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "Il rilascio si fa dal ramo main (ramo attuale: $branch)" }

git fetch --quiet --tags origin
if ($LASTEXITCODE -ne 0) { throw 'git fetch non riuscito' }
if (git tag --list $tag) { throw "Il tag $tag esiste già: scegliere un'altra versione" }
if (git ls-remote --tags origin "refs/tags/$tag") { throw "Il tag $tag esiste già su GitHub" }
$behind = git rev-list --count 'HEAD..origin/main'
if ([int]$behind -gt 0) { throw "Il ramo locale è indietro di $behind commit rispetto a origin/main: fare git pull" }
# In Windows PowerShell 5.1, con ErrorActionPreference=Stop, lo stderr ridiretto di un comando
# esterno («release not found») diventerebbe un errore che interrompe lo script: qui conta solo
# il codice di uscita.
$ErrorActionPreference = 'Continue'
gh release view $tag --repo $repo 2>&1 | Out-Null
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($releaseExists) { throw "La release $tag esiste già su GitHub" }

# ---------- versione ----------
# Lettura esplicita in UTF-8: Get-Content di Windows PowerShell 5.1 leggerebbe il file (senza BOM) come ANSI
# e, riscrivendolo, rovinerebbe gli accenti dei commenti (successo con la 1.2.0).
$csproj = [IO.File]::ReadAllText($csprojPath, [Text.Encoding]::UTF8)
$current = [regex]::Match($csproj, '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $current) { throw "<Version> non trovato in $csprojPath" }
$changed = $current -ne $Version
if ($changed) {
    Write-Host "Versione: $current → $Version"
    $csproj = [regex]::Replace($csproj, '<Version>[^<]+</Version>', "<Version>$Version</Version>", 1)
    [IO.File]::WriteAllText($csprojPath, $csproj, (New-Object Text.UTF8Encoding($false)))
} else {
    Write-Host "Versione già $Version nel csproj"
}

try {
    # ---------- build, test, installer ----------
    Run 'Build' { dotnet build RdpManager.sln -c Release -nologo }
    Run 'Test' { dotnet test RdpManager.sln -c Release --no-build -nologo }
    Run 'Pubblicazione e installer' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-installer.ps1') -ExpectedVersion $tag }
    Run 'Installer VPN generico' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-vpn-client.ps1') }
}
catch {
    if ($changed) { git checkout -- $csprojPath }   # nessuna modifica lasciata a metà
    throw
}

$setup = Join-Path $root "dist\RdpManager-Setup-$Version.exe"
$portable = Join-Path $root "dist\RdpManager-$Version-win-x64.exe"
$sums = Join-Path $root 'dist\SHA256SUMS.txt'
$linux = @("rdpmanager_${Version}_amd64.deb", "rdpmanager-${Version}-1.x86_64.rpm", "RdpManager-${Version}-x86_64.AppImage") |
    ForEach-Object { Join-Path $root "dist\$_" }
$vpnSetup = Join-Path $root 'dist\RdpManagerVPN-Setup.exe'
foreach ($f in (@($setup, $portable, $sums, $vpnSetup) + $linux)) { if (-not (Test-Path $f)) { throw "Manca $f" } }

# build-installer.ps1 non conosce l'installer VPN: la sua riga si aggiunge a SHA256SUMS.txt (senza duplicarla).
$sumLines = @([IO.File]::ReadAllLines($sums) | Where-Object { $_ -and $_ -notmatch '  RdpManagerVPN-Setup\.exe$' })
$sumLines += '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $vpnSetup).Hash.ToLowerInvariant(), 'RdpManagerVPN-Setup.exe'
[IO.File]::WriteAllText($sums, (($sumLines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

# SHA256SUMS.txt deve descrivere proprio i file che si pubblicano: l'app rifiuta un installer con hash diverso.
$expected = @{}
foreach ($line in [IO.File]::ReadAllLines($sums)) {
    if ($line -match '^([0-9a-f]{64})  (.+)$') { $expected[$Matches[2]] = $Matches[1] }
}
foreach ($f in (@($setup, $portable, $vpnSetup) + $linux)) {
    $name = Split-Path $f -Leaf
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $f).Hash.ToLowerInvariant()
    if ($expected[$name] -ne $hash) { throw "SHA256SUMS.txt non corrisponde a $name" }
}

# ---------- commit, tag, push ----------
if ($changed) {
    Run 'Commit' { git commit -m "Versione $Version" -- $csprojPath }
}
Run "Tag $tag" { git tag -a $tag -m "RdpManager $Version" }
Run 'Push di main' { git push origin main }
Run "Push del tag $tag" { git push origin $tag }

# ---------- release ----------
if (-not $Notes) {
    $Notes = Get-ReleaseNotes $Version
}
# Le note passano da un file UTF-8: come argomento, PowerShell 5.1 rovinerebbe a capo, virgolette e accenti.
$notesFile = Join-Path $env:TEMP "RdpManager-note-$Version.md"
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding($false)))
Run "Release $tag" { gh release create $tag $setup $portable $linux $vpnSetup $sums --repo $repo --title "RdpManager $Version" --notes-file $notesFile --verify-tag }
Remove-Item $notesFile -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Pubblicata: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
