# Wipe build artifacts. Run from repo root.
$ErrorActionPreference = "SilentlyContinue"
$Root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $Root

foreach ($p in "_pybuild","release","gui\bin","gui\obj") {
    if (Test-Path $p) {
        Write-Host "rm $p"
        Remove-Item -Recurse -Force $p
    }
}
Write-Host "Clean."
