# Prova: due costruzioni dei pacchetti Linux lanciate insieme (come fa il cancello di unione con le prove Linux in parallelo)
# devono riuscire entrambe. Prima della correzione condividevano artifacts\publish-linux e dist\ e si pestavano i piedi
# («cp: cannot stat .../artifacts/publish-linux/./Avalonia.Desktop.dll»).
# Uso: powershell -NoProfile -File scripts/test-linux-parallel.ps1 [-Count 2]

param([int]$Count = 2)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$logs = Join-Path ([IO.Path]::GetTempPath()) ('rdpmanager-parallel-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $logs | Out-Null
$build = Join-Path $PSScriptRoot 'build-linux.ps1'

$procs = 1..$Count | ForEach-Object {
    Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $build `
        -RedirectStandardOutput (Join-Path $logs "b$_.out") -RedirectStandardError (Join-Path $logs "b$_.err") -PassThru -WindowStyle Hidden
}
$procs | Wait-Process
$failed = 0
for ($i = 0; $i -lt $Count; $i++) {
    $code = $procs[$i].ExitCode
    Write-Host "costruzione $($i + 1): codice $code"
    if ($code -ne 0) {
        $failed++
        Get-Content (Join-Path $logs "b$($i + 1).err"), (Join-Path $logs "b$($i + 1).out") -Tail 25 | Write-Host
    }
}
if ($failed -gt 0) { Write-Host "FALLITA: $failed costruzioni su $Count (log in $logs)"; exit 1 }
Remove-Item -Recurse -Force $logs -ErrorAction SilentlyContinue
Write-Host "OK: $Count costruzioni parallele riuscite"
