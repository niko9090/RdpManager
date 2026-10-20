# Pubblica una nuova versione di RdpManager su GitHub.
#
# Uso (dalla cartella del progetto, su un PC con .NET 8 SDK, Inno Setup 6 e gh autenticato):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version 1.1.0
#   (facoltativo) -Notes "Novità di questa versione"
#
# Passi: controlli → <Version> nel csproj → build → test → pubblicazione + installer (build-installer.ps1)
# → commit «Versione X.Y.Z» (se la versione è cambiata) → tag vX.Y.Z → push di main e del tag
# → gh release create con l'installer e l'eseguibile portatile.
# Si ferma senza fare nulla se ci sono modifiche non salvate, se non si è su main o se il tag esiste già.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$repo = 'niko9090/RdpManager'
$csprojPath = Join-Path $root 'src\RdpManager\RdpManager.csproj'

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
gh release view $tag --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "La release $tag esiste già su GitHub" }

# ---------- versione ----------
$csproj = Get-Content -Raw $csprojPath
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
}
catch {
    if ($changed) { git checkout -- $csprojPath }   # nessuna modifica lasciata a metà
    throw
}

$setup = Join-Path $root "dist\RdpManager-Setup-$Version.exe"
$portable = Join-Path $root "dist\RdpManager-$Version-win-x64.exe"
foreach ($f in $setup, $portable) { if (-not (Test-Path $f)) { throw "Manca $f" } }

# ---------- commit, tag, push ----------
if ($changed) {
    Run 'Commit' { git commit -m "Versione $Version" -- $csprojPath }
}
Run "Tag $tag" { git tag -a $tag -m "RdpManager $Version" }
Run 'Push di main' { git push origin main }
Run "Push del tag $tag" { git push origin $tag }

# ---------- release ----------
if (-not $Notes) {
    $Notes = "RdpManager $Version.`n`n" +
             "Installazione: scarica ed esegui RdpManager-Setup-$Version.exe (per utente, non servono diritti di amministratore; si installa sopra la versione precedente).`n" +
             "RdpManager-$Version-win-x64.exe è la versione portatile, senza installazione.`n" +
             "Le connessioni salvate in %APPDATA%\RdpManager restano anche aggiornando o disinstallando."
}
Run "Release $tag" { gh release create $tag $setup $portable --repo $repo --title "RdpManager $Version" --notes $Notes --verify-tag }

Write-Host ""
Write-Host "Pubblicata: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
