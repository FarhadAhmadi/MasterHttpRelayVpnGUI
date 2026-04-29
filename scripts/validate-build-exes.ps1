[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipSelfTest
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "!!  $msg" -ForegroundColor Red; exit 1 }
function Info($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

Step "Validating prerequisites"

# --------------------------------------------------------------------
# SAFE PYTHON DETECTION (avoids Microsoft Store alias automatically)
# --------------------------------------------------------------------
$pythonCmd = $null

# Try "python" but only if not the MS Store stub
if (Get-Command python -ErrorAction SilentlyContinue) {
    $pythonPath = (Get-Command python).Source
    if ($pythonPath -notlike "*Microsoft\WindowsApps*") {
        $pythonCmd = $pythonPath
    }
}

# Try "py" (Python Launcher) if python not usable
if (-not $pythonCmd -and (Get-Command py -ErrorAction SilentlyContinue)) {
    $pythonCmd = (Get-Command py).Source
}

# Fail if no usable python found
if (-not $pythonCmd) {
    Fail "Python not found (neither python.exe nor py.exe available)."
}

Info ("python  : " + ((& $pythonCmd --version 2>&1) -replace 'Python\s+',''))

# --------------------------------------------------------------------
# Check other prerequisites
# --------------------------------------------------------------------
foreach ($cmd in @("dotnet", "powershell")) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        Fail "$cmd is not available on PATH."
    }
}

Info ("dotnet  : " + (& dotnet --version))

# --------------------------------------------------------------------
# Run full build pipeline
# --------------------------------------------------------------------
Step "Running full build pipeline"
$buildArgs = @()
if ($Clean) { $buildArgs += "-Clean" }
if ($SkipSelfTest) { $buildArgs += "-NoSelfTest" }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build\build.ps1") @buildArgs
if ($LASTEXITCODE -ne 0) {
    Fail "Build pipeline failed with exit code $LASTEXITCODE."
}

# --------------------------------------------------------------------
# Collect executable details
# --------------------------------------------------------------------
Step "Collecting executable details"
$targets = @(
    @{ Name = "GUI";  Path = Join-Path $root "release\MasterRelayVPN\MasterRelayVPN.exe" },
    @{ Name = "Core"; Path = Join-Path $root "release\MasterRelayVPN\core\MasterRelayCore.exe" }
)

$report = @()
foreach ($t in $targets) {
    $path = $t.Path
    if (-not (Test-Path $path)) {
        Fail "$($t.Name) executable not found: $path"
    }
    $item = Get-Item $path
    $ver = (Get-Item $path).VersionInfo
    $sha = (Get-FileHash $path -Algorithm SHA256).Hash

    $report += [PSCustomObject]@{
        Name            = $t.Name
        Path            = $item.FullName
        Directory       = $item.DirectoryName
        SizeBytes       = $item.Length
        SizeMB          = [math]::Round($item.Length / 1MB, 2)
        LastWriteTime   = $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
        FileVersion     = $ver.FileVersion
        ProductVersion  = $ver.ProductVersion
        SHA256          = $sha
    }
}

$reportDir = Join-Path $root "release\MasterRelayVPN"
$jsonPath = Join-Path $reportDir "exe-details.json"
$txtPath = Join-Path $reportDir "exe-details.txt"
$cmdPath = Join-Path $reportDir "how-to-run.txt"
$relNotesSrc = Join-Path $root "docs\RELEASE_1.5.0.md"
$relNotesDst = Join-Path $reportDir "RELEASE_NOTES_1.5.0.md"

# JSON output
$report | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 $jsonPath

# TXT output
$lines = @()
$lines += "MasterRelayVPN build artifacts"
$lines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += ""

foreach ($r in $report) {
    $lines += "$($r.Name) executable"
    $lines += "  Path          : $($r.Path)"
    $lines += "  Directory     : $($r.Directory)"
    $lines += "  Size          : $($r.SizeMB) MB ($($r.SizeBytes) bytes)"
    $lines += "  Last modified : $($r.LastWriteTime)"
    $lines += "  File version  : $($r.FileVersion)"
    $lines += "  Product ver   : $($r.ProductVersion)"
    $lines += "  SHA256        : $($r.SHA256)"
    $lines += ""
}

$lines += "Also generated:"
$lines += "  release\\MasterRelayVPN.zip"
$lines += "  release\\MasterRelayVPN\\README.txt (if available)"

$lines | Set-Content -Encoding UTF8 $txtPath

# how-to-run instructions
$runLines = @(
    "Run GUI:",
    "  ""$($report[0].Path)""",
    "",
    "Run Core manually with config:",
    "  ""$($report[1].Path)"" --config ""$root\\config.json""",
    "",
    "Generate CA with Core:",
    "  ""$($report[1].Path)"" --gen-ca"
)
$runLines | Set-Content -Encoding UTF8 $cmdPath

# Copy release notes
if (Test-Path $relNotesSrc) {
    Copy-Item $relNotesSrc $relNotesDst -Force
}

# Output summary
Step "Build complete"
$report | Format-Table Name, SizeMB, LastWriteTime, Path -AutoSize

Write-Host ""
Write-Host "Details files:" -ForegroundColor Green
Write-Host "  $txtPath"
Write-Host "  $jsonPath"
Write-Host "  $cmdPath"
if (Test-Path $relNotesDst) { Write-Host "  $relNotesDst" }
