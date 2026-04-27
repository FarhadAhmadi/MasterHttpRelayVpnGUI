[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path "$PSScriptRoot\..").Path
$Project = Join-Path $Root "gui\MasterRelayVPN.csproj"
$Artifacts = Join-Path $Root $OutputDir
$PublishDir = Join-Path $Artifacts "publish"
$PackageDir = Join-Path $Artifacts "package"
$ZipPath = Join-Path $PackageDir "MasterRelayVPN-$Runtime.zip"
$ShaPath = Join-Path $PackageDir "SHA256SUMS.txt"

if (-not (Test-Path $Project)) {
    throw "Project file not found: $Project"
}

foreach ($dir in @($Artifacts, $PublishDir, $PackageDir)) {
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
}

dotnet restore $Project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

dotnet publish $Project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force -CompressionLevel Optimal

$hash = Get-FileHash $ZipPath -Algorithm SHA256
"$($hash.Hash)  $(Split-Path $ZipPath -Leaf)" | Set-Content -Encoding ASCII $ShaPath

Write-Host "Release package created:"
Write-Host "  $ZipPath"
Write-Host "  $ShaPath"
