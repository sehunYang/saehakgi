#requires -Version 5.1
<#
  Packages saehakgi for USB distribution.

  Produces dist/ containing:
    saehakgi.exe         - the GUI app (self-contained, single file, requires admin)
    Saehakgi.Host.exe    - the browser native-messaging host (self-contained, single file)
    extension/           - the MV3 browser extension to load in Chrome/Edge

  Self-contained => target PCs need NO .NET runtime installed.
  Copy the whole dist/ folder onto the USB.

  Usage:  powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 0.1.0]
#>
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$rid  = 'win-x64'

Write-Host "repo : $root"
Write-Host "dist : $dist"
Write-Host "rid  : $rid`n"

if (Test-Path $dist) {
  try {
    Remove-Item $dist -Recurse -Force -ErrorAction Stop
  } catch {
    Write-Host "`n[!] dist 폴더를 정리하지 못했습니다." -ForegroundColor Yellow
    Write-Host "    실행 중인 saehakgi.exe / Saehakgi.Host.exe(또는 확장이 연 브라우저)를 모두 닫고 다시 실행하세요."
    Write-Host "    상세: $($_.Exception.Message)"
    exit 1
  }
}
New-Item -ItemType Directory -Force $dist | Out-Null

$flags = @(
  '-c', 'Release',
  '-r', $rid,
  '--self-contained', 'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none',
  '-p:DebugSymbols=false',
  '-o', $dist
)

Write-Host '=== publishing Saehakgi.App (GUI) ==='
dotnet publish (Join-Path $root 'src\Saehakgi.App\Saehakgi.App.csproj') @flags
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

Write-Host "`n=== publishing Saehakgi.Host (native host) ==="
dotnet publish (Join-Path $root 'src\Saehakgi.Host\Saehakgi.Host.csproj') @flags
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed' }

Write-Host "`n=== bundling extension + user guide ==="
Copy-Item (Join-Path $root 'extension') (Join-Path $dist 'extension') -Recurse -Force

$assets = Join-Path $root 'build\dist-assets'
if (Test-Path $assets) {
  # Skip dot-folders (tooling state such as .omc) so they never reach the USB.
  Get-ChildItem $assets -Force |
    Where-Object { $_.Name -notlike '.*' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $dist -Recurse -Force }
}

# Keep only what the USB needs (drop stray config/pdb if any).
Get-ChildItem $dist -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "`n=== zipping ==="
$zip = Join-Path $root "saehakgi-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip
Write-Host ("zip: {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

Write-Host "`n=== dist contents ==="
Get-ChildItem $dist -Recurse |
  Where-Object { -not $_.PSIsContainer } |
  ForEach-Object {
    $rel = $_.FullName.Substring($dist.Length + 1)
    '{0,10:N0}  {1}' -f $_.Length, $rel
  }

Write-Host "`nDone. Copy the 'dist' folder to your USB."
