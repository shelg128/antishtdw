param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\QemuGaGuard\QemuGaGuard.csproj"
$dist = Join-Path $root "dist"
$publishDir = Join-Path $dist "publish"
$portableZip = Join-Path $dist "QemuGaGuard-portable-$Runtime.zip"

Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishDir | Out-Null

dotnet publish $project `
  -c $Configuration `
  -r $Runtime `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeNativeLibrariesForSelfExtract=true `
  /p:DebugType=None `
  /p:DebugSymbols=false `
  -o $publishDir

$exePath = Join-Path $publishDir "QemuGaGuard.exe"
if (-not (Test-Path $exePath)) {
    throw "Publish failed. Missing $exePath"
}

$relativeJsonProbe = "dist\\state-probe.json"
$jsonProbe = Join-Path $root "dist\state-probe.json"
Remove-Item -Force $jsonProbe -ErrorAction SilentlyContinue
$probe = Start-Process -FilePath $exePath -WorkingDirectory $root -ArgumentList @("--export-state", $relativeJsonProbe) -PassThru -Wait
if ($probe.ExitCode -ne 0) {
    throw "Probe failed. Exit code $($probe.ExitCode)"
}

$deadline = (Get-Date).AddSeconds(10)
while (-not (Test-Path $jsonProbe) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
}

if (-not (Test-Path $jsonProbe)) {
    throw "Probe failed. Missing $jsonProbe"
}

if (Test-Path $portableZip) {
    Remove-Item -Force $portableZip
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $portableZip

Write-Host "Published:"
Write-Host "  $exePath"
Write-Host "  $portableZip"
Write-Host "  $jsonProbe"
