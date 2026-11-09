param(
    [string]$Version = "1.0.0",
    [string]$OutputRoot = "",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repo "dist"
}
$out = [System.IO.Path]::GetFullPath($OutputRoot)
$publish = Join-Path $out "publish"

# Writes SHA256SUMS.txt covering the published exe and the installer (avoids manual drift).
function Write-Sums([string]$SumRoot, [string]$PublishDir) {
    $targets = @()
    $targets += Get-ChildItem $PublishDir -Filter "DiskSpaceGuard.exe" -File -ErrorAction SilentlyContinue
    $targets += Get-ChildItem $SumRoot -Filter "DiskSpaceGuard-Setup-*.exe" -File -ErrorAction SilentlyContinue
    if ($targets.Count -eq 0) { return }
    $lines = foreach ($f in $targets) {
        "{0}  {1}" -f (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower(), $f.Name
    }
    $sums = Join-Path $SumRoot "SHA256SUMS.txt"
    $lines | Set-Content -Path $sums -Encoding ascii
    Write-Host "Wrote $sums"
}

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$dotnet = $null
if ($env:DOTNET_ROOT) {
    $candidate = Join-Path $env:DOTNET_ROOT "dotnet.exe"
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { throw "dotnet.exe not found. Install the .NET 8 SDK." }

Write-Host "Publishing self-contained single-file win-x64..."
& $dotnet publish (Join-Path $repo "src\DiskSpaceGuard\DiskSpaceGuard.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exe = Join-Path $publish "DiskSpaceGuard.exe"
$files = @(Get-ChildItem $publish -File)
if (-not (Test-Path $exe) -or $files.Count -ne 1) {
    throw "Expected exactly one single-file executable in $publish"
}
Write-Host ("Published: {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

if ($SkipInstaller) {
    Write-Sums -SumRoot $out -PublishDir $publish
    return
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Warning "Inno Setup 6/7 (ISCC.exe) not found. The single-file exe is ready; install Inno Setup to build the installer."
    Write-Sums -SumRoot $out -PublishDir $publish
    return
}

Write-Host "Building installer with $iscc..."
& $iscc (Join-Path $PSScriptRoot "DiskSpaceGuard.iss") `
    "/DPublishDir=$publish" `
    "/DOutputDir=$out" `
    "/DMyAppVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Write-Sums -SumRoot $out -PublishDir $publish
Get-ChildItem $out -Filter "DiskSpaceGuard-Setup-*.exe" | Select-Object FullName, Length
