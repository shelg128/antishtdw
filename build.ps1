param()

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\\main.cpp"
$dist = Join-Path $root "dist"
$exe = Join-Path $dist "PowerMenuGuard.exe"
$readmeSource = Join-Path $root "README.md"
$readmeTarget = Join-Path $dist "README.md"
$enableCmd = Join-Path $dist "Enable Power Menu Guard.cmd"
$disableCmd = Join-Path $dist "Disable Power Menu Guard.cmd"
$statusCmd = Join-Path $dist "Status Power Menu Guard.cmd"
$installerScript = Join-Path $root "installer\\PowerMenuGuardSetup.nsi"
$installerOutput = Join-Path $dist "Power Menu Guard Setup.exe"
$portableZip = Join-Path $dist "Power Menu Guard Portable x64.zip"
$windowsKitsRoot = "C:\Program Files (x86)\Windows Kits\10\Redist"

if (-not (Test-Path $src)) {
    throw "Missing source file: $src"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Get-ChildItem -Path $dist -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

$gpp = (Get-Command g++ -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($gpp)) {
    throw "g++ not found in PATH."
}

Write-Host "Compiling PowerMenuGuard.exe"
& $gpp $src `
    -std=c++17 `
    -municode `
    -mwindows `
    -DUNICODE `
    -D_UNICODE `
    -O2 `
    -s `
    -static `
    -static-libgcc `
    -static-libstdc++ `
    -o $exe `
    -ladvapi32 `
    -lnetapi32 `
    -lshell32 `
    -luser32 `
    -lgdi32 `
    -lcomctl32
if ($LASTEXITCODE -ne 0) {
    throw "g++ build failed with exit code $LASTEXITCODE."
}

$imports = & objdump -p $exe | Select-String 'DLL Name'
if ($LASTEXITCODE -ne 0) {
    throw "Failed to inspect EXE imports."
}

$forbiddenImports = @('libstdc++-6.dll', 'libgcc_s_seh-1.dll', 'libwinpthread-1.dll')
foreach ($forbiddenImport in $forbiddenImports) {
    if ($imports -match [regex]::Escape($forbiddenImport)) {
        throw "Build still depends on $forbiddenImport."
    }
}

$ucrtCandidates = @()
if (Test-Path $windowsKitsRoot) {
    $versionedCandidates = Get-ChildItem -Path $windowsKitsRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "ucrt\\DLLs\\x64" }
    $ucrtCandidates += $versionedCandidates
    $ucrtCandidates += Join-Path $windowsKitsRoot "ucrt\\DLLs\\x64"
}

$ucrtSource = $ucrtCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($ucrtSource)) {
    throw "UCRT redistributable x64 folder not found under Windows Kits."
}

Write-Host "Copying x64 UCRT runtime from $ucrtSource"
Copy-Item -Path (Join-Path $ucrtSource "*.dll") -Destination $dist -Force

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

if (Test-Path $portableZip) {
    Remove-Item -LiteralPath $portableZip -Force
}

$portableFiles = @(
    (Join-Path $dist "PowerMenuGuard.exe"),
    (Join-Path $dist "*.dll"),
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
