param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\\main.cpp"
$dist = Join-Path $root "dist"
$exe = Join-Path $dist "PowerMenuGuard.exe"
$objectFile = Join-Path $dist "PowerMenuGuard.obj"
$portableZip = Join-Path $dist "Power Menu Guard Portable x64.zip"
$readmeSource = Join-Path $root "README.md"
$readmeTarget = Join-Path $dist "README.md"
$enableCmd = Join-Path $dist "Enable Power Menu Guard.cmd"
$disableCmd = Join-Path $dist "Disable Power Menu Guard.cmd"
$statusCmd = Join-Path $dist "Status Power Menu Guard.cmd"
$installerScript = Join-Path $root "installer\\PowerMenuGuardSetup.nsi"
$installerOutput = Join-Path $dist "Power Menu Guard Setup.exe"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\\Installer\\vswhere.exe"

if (-not (Test-Path $src)) {
    throw "Missing source file: $src"
}

if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found. Install Visual Studio Build Tools 2022 with MSVC x64/x86 build tools."
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Remove-Item -LiteralPath $objectFile, $exe, $portableZip -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $dist "PowerMenuGuard-msvc.exe") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $root "main.obj") -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $dist -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$vsInstallJson = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($vsInstallJson)) {
    throw "Unable to locate a Visual Studio Build Tools installation with MSVC x64 tools."
}

$vsInstall = $vsInstallJson | ConvertFrom-Json | Select-Object -First 1
if ($null -eq $vsInstall) {
    throw "No Visual Studio Build Tools installation with MSVC x64 tools was found."
}

$vsDevCmd = Join-Path $vsInstall.installationPath "Common7\\Tools\\VsDevCmd.bat"
if (-not (Test-Path $vsDevCmd)) {
    throw "VsDevCmd.bat not found: $vsDevCmd"
}

$buildCmd = Join-Path $env:TEMP "power-menu-guard-build-msvc.cmd"
@"
@echo off
call "$vsDevCmd" -arch=amd64 -host_arch=amd64 >nul
cl /nologo /std:c++17 /EHsc /O2 /MT /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN /Fo:"$objectFile" /Fe:"$exe" "$src" advapi32.lib netapi32.lib shell32.lib user32.lib gdi32.lib comctl32.lib /link /SUBSYSTEM:WINDOWS /OPT:REF /OPT:ICF
"@ | Set-Content -LiteralPath $buildCmd -Encoding ASCII

try {
    Write-Host "Compiling PowerMenuGuard.exe with MSVC"
    cmd /c $buildCmd
    if ($LASTEXITCODE -ne 0) {
        throw "MSVC build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $buildCmd -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $objectFile -Force -ErrorAction SilentlyContinue
}

$imports = & objdump -p $exe | Select-String "DLL Name"
if ($LASTEXITCODE -ne 0) {
    throw "Failed to inspect EXE imports."
}

$forbiddenImports = @(
    "libstdc++-6.dll",
    "libgcc_s_seh-1.dll",
    "libwinpthread-1.dll",
    "VCRUNTIME140.dll",
    "MSVCP140.dll",
    "ucrtbase.dll",
    "api-ms-win-crt-"
)

foreach ($forbiddenImport in $forbiddenImports) {
    if ($imports -match [regex]::Escape($forbiddenImport)) {
        throw "Build still depends on $forbiddenImport."
    }
}

Copy-Item -LiteralPath $readmeSource -Destination $readmeTarget -Force
Set-Content -LiteralPath $enableCmd -Value '@echo off
start "" "%~dp0PowerMenuGuard.exe" --enable
' -Encoding ASCII
Set-Content -LiteralPath $disableCmd -Value '@echo off
start "" "%~dp0PowerMenuGuard.exe" --disable
' -Encoding ASCII
Set-Content -LiteralPath $statusCmd -Value '@echo off
start /wait "" "%~dp0PowerMenuGuard.exe" --status
' -Encoding ASCII

$portableFiles = @(
    (Join-Path $dist "PowerMenuGuard.exe"),
    (Join-Path $dist "README.md"),
    (Join-Path $dist "Enable Power Menu Guard.cmd"),
    (Join-Path $dist "Disable Power Menu Guard.cmd"),
    (Join-Path $dist "Status Power Menu Guard.cmd")
)

Write-Host "Packing portable ZIP"
Compress-Archive -Path $portableFiles -DestinationPath $portableZip -CompressionLevel Optimal

$makensisCandidates = @(
    "C:\Program Files (x86)\NSIS\makensis.exe",
    "C:\Program Files (x86)\NSIS\Bin\makensis.exe"
)

$makensis = $makensisCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($makensis)) {
    Write-Host "makensis.exe not found. EXE build completed without installer."
    exit 0
}

Write-Host "Building NSIS installer"
& $makensis $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "NSIS build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $installerOutput)) {
    throw "Installer output missing: $installerOutput"
}

Write-Host "Build complete:"
Write-Host "  EXE      : $exe"
Write-Host "  INSTALLER: $installerOutput"
Write-Host "  PORTABLE : $portableZip"
