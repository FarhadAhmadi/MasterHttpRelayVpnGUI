# MasterRelayVPN — one-command build.
#
#   powershell -ExecutionPolicy Bypass -File build\build.ps1
#
# Steps:
#   1.  prerequisites check (python, dotnet)
#   2.  create / refresh _pybuild venv, install pinned deps
#   3.  stage src\ + core\ into _pybuild\stage\, build with PyInstaller spec
#   4.  dotnet publish the WPF GUI (single-file, self-contained, win-x64)
#   5.  copy artifacts into release\MasterRelayVPN\
#   6.  self-test: run the bundled exe, verify --gen-ca and a 3s listen
#   7.  zip into release\MasterRelayVPN.zip

[CmdletBinding()]
param(
    [switch]$NoSelfTest,
    [switch]$SkipGui,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $Root

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
function Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "!!  $msg" -ForegroundColor Red; exit 1 }

# ----------------------------------------------------------
# Python detection — SAFE (no MS Store alias)
# ----------------------------------------------------------
function Get-SafePython {
    # Try python.exe first
    if (Get-Command python -ErrorAction SilentlyContinue) {
        $p = (Get-Command python).Source
        if ($p -and ($p -notlike "*Microsoft\WindowsApps*")) {
            return $p
        }
    }
    # Fallback to py.exe
    if (Get-Command py -ErrorAction SilentlyContinue) {
        return (Get-Command py).Source
    }
    return $null
}

# ------------------------------------------------------------------
# 0. Optional clean
# ------------------------------------------------------------------
if ($Clean) {
    Step "Clean"
    foreach ($p in "_pybuild","release","gui\bin","gui\obj") {
        if (Test-Path $p) { Remove-Item -Recurse -Force $p; Note "rm $p" }
    }
}

# ------------------------------------------------------------------
# 1. Prerequisites
# ------------------------------------------------------------------
Step "Checking prerequisites"

$pythonCmd = Get-SafePython
if (-not $pythonCmd) { Fail "Python not found (python.exe / py.exe missing or aliasing to MS Store)" }

$pyVersion = (& $pythonCmd --version 2>&1) -replace "Python\s+",""
Note "python  : $pyVersion"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail "dotnet not found on PATH"
}
$dotnetVer = (& dotnet --version)
Note "dotnet  : $dotnetVer"

# ------------------------------------------------------------------
# 2. Python venv
# ------------------------------------------------------------------
Step "Python venv"
$venv = Join-Path $Root "_pybuild\.venv"
$venvPy = Join-Path $venv "Scripts\python.exe"

if (-not (Test-Path $venvPy)) {
    New-Item -ItemType Directory -Force (Split-Path $venv) | Out-Null
    & $pythonCmd -m venv $venv
    if ($LASTEXITCODE -ne 0) { Fail "venv creation failed" }
    Ok "created $venv"
} else {
    Note "reusing $venv"
}

& $venvPy -m pip install --upgrade pip --quiet --disable-pip-version-check
if ($LASTEXITCODE -ne 0) { Fail "pip upgrade failed" }

& $venvPy -m pip install --quiet --disable-pip-version-check -r (Join-Path $Root "requirements.txt")
if ($LASTEXITCODE -ne 0) { Fail "pip install failed" }
Ok "deps installed"

# ------------------------------------------------------------------
# 3. Stage Python sources & build core
# ------------------------------------------------------------------
Step "Staging Python sources"
$stage = Join-Path $Root "_pybuild\stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item (Join-Path $Root "src\*.py")  $stage
Copy-Item (Join-Path $Root "core\*.py") $stage
Copy-Item (Join-Path $Root "build\MasterRelayCore.spec") $stage

$required = @(
    "main_gui.py","ca_path.py","stats.py","log_filter.py","net_patches.py","gui_bridge.py",
    "cert_installer.py","mitm.py","proxy_server.py","domain_fronter.py","h2_transport.py",
    "MasterRelayCore.spec"
)
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $stage $f))) { Fail "missing in stage: $f" }
}
Ok "$($required.Count) files staged"

Step "PyInstaller"
Push-Location $stage
try {
    & $venvPy -m PyInstaller --noconfirm --clean MasterRelayCore.spec
    if ($LASTEXITCODE -ne 0) { Fail "PyInstaller failed" }
} finally { Pop-Location }

$coreExe = Join-Path $stage "dist\MasterRelayCore.exe"
if (-not (Test-Path $coreExe)) { Fail "MasterRelayCore.exe not produced at $coreExe" }
$coreSize = [math]::Round((Get-Item $coreExe).Length / 1MB, 1)
Ok "MasterRelayCore.exe ($coreSize MB)"

# ------------------------------------------------------------------
# 4. GUI build
# ------------------------------------------------------------------
$guiExe = $null
if (-not $SkipGui) {
    Step "WPF GUI"
    Push-Location (Join-Path $Root "gui")
    try {
        & dotnet publish -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            --nologo
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed" }
    } finally { Pop-Location }

    $guiExe = Join-Path $Root "gui\bin\Release\net8.0-windows\win-x64\publish\MasterRelayVPN.exe"
    if (-not (Test-Path $guiExe)) { Fail "MasterRelayVPN.exe not produced" }
    $guiSize = [math]::Round((Get-Item $guiExe).Length / 1MB, 1)
    Ok "MasterRelayVPN.exe ($guiSize MB)"
}

# ------------------------------------------------------------------
# 5. Stage release folder
# ------------------------------------------------------------------
Step "Assembling release\MasterRelayVPN"
$release = Join-Path $Root "release\MasterRelayVPN"
if (Test-Path $release) { Remove-Item -Recurse -Force $release }
New-Item -ItemType Directory -Force $release | Out-Null
New-Item -ItemType Directory -Force (Join-Path $release "core") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $release "data") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $release "data\cert") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $release "data\logs") | Out-Null

Copy-Item $coreExe (Join-Path $release "core\MasterRelayCore.exe")
if ($guiExe) { Copy-Item $guiExe (Join-Path $release "MasterRelayVPN.exe") }

$readme = Join-Path $Root "docs\README.txt"
if (Test-Path $readme) { Copy-Item $readme (Join-Path $release "README.txt") }

Ok "staged $release"

# ------------------------------------------------------------------
# 6. Self-test
# ------------------------------------------------------------------
if (-not $NoSelfTest) {
    Step "Self-test"

    # 6a. --gen-ca
    $testCaDir = Join-Path $Root "_pybuild\selftest_ca"
    if (Test-Path $testCaDir) { Remove-Item -Recurse -Force $testCaDir }
    New-Item -ItemType Directory -Force $testCaDir | Out-Null

    $env:MRELAY_CA_DIR = $testCaDir
    & (Join-Path $release "core\MasterRelayCore.exe") "--gen-ca" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "core --gen-ca exited with code $LASTEXITCODE" }
    if (-not (Test-Path (Join-Path $testCaDir "ca.crt"))) { Fail "CA file not written" }
    if (-not (Test-Path (Join-Path $testCaDir "ca.key"))) { Fail "CA key not written" }
    Ok "[1/3] --gen-ca OK"

    # 6b. fingerprint persists
    $sha1 = (Get-FileHash (Join-Path $testCaDir "ca.crt") -Algorithm SHA256).Hash
    & (Join-Path $release "core\MasterRelayCore.exe") "--gen-ca" | Out-Null
    $sha2 = (Get-FileHash (Join-Path $testCaDir "ca.crt") -Algorithm SHA256).Hash
    if ($sha1 -ne $sha2) { Fail "CA fingerprint mismatch" }
    Ok "[2/3] CA persists"

    # 6c. boot test
    $cfgPath = Join-Path $Root "_pybuild\selftest_config.json"
    $selfTestJson = @"
{
  "mode": "apps_script",
  "google_ip": "216.239.38.120",
  "front_domain": "www.google.com",
  "script_id": "AKfycbXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
  "auth_key": "selftest_secret",
  "listen_host": "127.0.0.1",
  "listen_port": 18099,
  "log_level": "INFO",
  "verify_ssl": true,
  "enable_http2": false,
  "enable_chunked": true,
  "chunk_size": 131072,
  "max_parallel": 4,
  "fragment_size": 16384
}
"@
    # Use UTF-8 without BOM because the Python loader reads with encoding="utf-8".
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($cfgPath, $selfTestJson, $utf8NoBom)

    $proc = Start-Process -FilePath (Join-Path $release "core\MasterRelayCore.exe") `
        -ArgumentList @("--config", $cfgPath) `
        -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $Root "_pybuild\selftest_out.log") `
        -RedirectStandardError  (Join-Path $Root "_pybuild\selftest_err.log")

    Start-Sleep -Seconds 3

    $listening = $false
    try {
        $tcp = New-Object Net.Sockets.TcpClient
        $tcp.Connect("127.0.0.1", 18099)
        $listening = $tcp.Connected
        $tcp.Close()
    } catch { $listening = $false }

    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit(2000) | Out-Null
    }

    if (-not $listening) {
        $errLog = Get-Content (Join-Path $Root "_pybuild\selftest_err.log") -Raw -ErrorAction SilentlyContinue
        Note "stderr:"
        Write-Host $errLog -ForegroundColor DarkGray
        Fail "core did not accept TCP on 127.0.0.1:18099"
    }

    Ok "[3/3] core boots fine"

    Remove-Item Env:\MRELAY_CA_DIR -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------
# 7. release.zip
# ------------------------------------------------------------------
Step "Packaging release.zip"
$zip = Join-Path $Root "release\MasterRelayVPN.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $release -DestinationPath $zip -CompressionLevel Optimal
$zipSize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Ok "MasterRelayVPN.zip ($zipSize MB)"

Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  Folder : $release"
Write-Host "  Archive: $zip"
Write-Host ""
Get-ChildItem $release -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($release.Length + 1)
    "  {0,-44} {1,8:N0} bytes" -f $rel, $_.Length | Write-Host
}
