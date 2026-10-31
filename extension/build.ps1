$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$DefaultConfig = Join-Path (Split-Path $Root -Parent) "config"
$ConfigRoot = if ($env:AUTOSOURCARR_HOST_CONFIG_DIR) { $env:AUTOSOURCARR_HOST_CONFIG_DIR } elseif (Test-Path $DefaultConfig) { $DefaultConfig } else { Join-Path $Root "config" }
New-Item -ItemType Directory -Force -Path $ConfigRoot | Out-Null

docker run --rm `
  -v "${Root}:/workspace" `
  -v "${ConfigRoot}:/autosourcarr-config" `
  -e AUTOSOURCARR_CONFIG_DIR=/autosourcarr-config `
  -w /workspace `
  ghcr.io/cirruslabs/android-sdk:35 `
  bash /workspace/extension/build-in-container.sh

$BuildExitCode = $LASTEXITCODE
if ($BuildExitCode -ne 0) {
  Write-Error "Reader build failed (Docker exit $BuildExitCode)." -ErrorAction Continue
  exit $BuildExitCode
}

Write-Host "Build output is in extension/dist"
Write-Host "Persistent credentials/signing state: $ConfigRoot"
