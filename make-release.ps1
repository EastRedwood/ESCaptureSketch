# Builds the distribution zip: release\ESCaptureSketch-v<version>-win-x64.zip
# Framework-dependent (needs the .NET 10 Desktop Runtime), no debug symbols.
# Usage: powershell -ExecutionPolicy Bypass -File .\make-release.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = '1.0.' + (Get-Content .build_number -Raw).Trim()
$work = Join-Path $PSScriptRoot 'release'
$stage = Join-Path $work 'ESCaptureSketch'
$zip = Join-Path $work "ESCaptureSketch-v$version-win-x64.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

# SkipShellRegister: publishing must not point the .escs association at the staging folder.
dotnet publish ESCaptureSketch.csproj -c Release -r win-x64 --self-contained false -o $stage --nologo -v q `
    -p:SkipShellRegister=true -p:DebugType=none -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Everything a user needs must be in the package.
$required = 'ESCaptureSketch.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses\SkiaSharp-THIRD-PARTY-NOTICES.txt',
    'READ-ME-FIRST.txt', 'manual-en.txt',
    'ESCaptureSketch-RemoveAutostart.bat', 'ESCaptureSketch-RemoveFileType.bat'
foreach ($f in $required) {
    if (-not (Test-Path (Join-Path $stage $f))) { throw "missing in package: $f" }
}
if (-not (Get-ChildItem $stage -Filter '*.txt' | Where-Object { $_.Name -notin 'READ-ME-FIRST.txt', 'manual-en.txt' })) { throw "missing in package: Korean manual" }
if (Get-ChildItem $stage -Recurse -Filter *.pdb) { throw "debug symbols in package" }

Compress-Archive -Path $stage -DestinationPath $zip
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
(Get-FileHash $zip -Algorithm SHA256).Hash
