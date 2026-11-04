<#
.SYNOPSIS
  Migrates a snapshot of AutoSourcarr 0.4.x to 0.5.0 on this PC and writes a parity report.

.DESCRIPTION
  Source is either a 0.4.5 instance backup file (System -> Backups -> Download; the passphrase is asked and never
  stored) or a folder holding copies of the 0.4.x 'data' and 'config' folders (and 'database' when DATABASE_DIR was
  elsewhere). The source is only read. Destination must be a new or empty folder; it receives data\ and config\
  (mount them in docker-compose.0.5.0.yml), original-0.4\ (the untouched copy the comparison used) and
  parity-report.md / parity-report.json. Needs the .NET 10 SDK; no Docker, no network access to your server.

.EXAMPLE
  .\scripts\migrate-from-0.4.ps1 D:\snapshots\autosourcarr-0.4.5.ascbackup D:\autosourcarr-0.5.0
.EXAMPLE
  .\scripts\migrate-from-0.4.ps1 D:\snapshots\autosourcarr-0.4 D:\autosourcarr-0.5.0
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Source,
    [Parameter(Mandatory = $true, Position = 1)][string]$Destination,
    # Optional: the 0.4.x Compose file, when its environment: sets DATA_DIR or DATABASE_DIR (and the service for two instances).
    [string]$Compose,
    [string]$Service
)
$ErrorActionPreference = 'Stop'
# A profile that turns native exit codes into errors would hide the tool's own exit code: this script reads it itself.
$PSNativeCommandUseErrorActionPreference = $false
function Fail([string]$Message, [int]$Code = 2) { [Console]::Error.WriteLine($Message); exit $Code }
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src/AutoSourcarr.Api/AutoSourcarr.Api.csproj'
$dll = Join-Path $repo 'src/AutoSourcarr.Api/bin/Release/net10.0/AutoSourcarr.Api.dll'

if (-not (Test-Path -LiteralPath $Source)) { Fail "The source does not exist: $Source" 2 }
$Source = (Resolve-Path -LiteralPath $Source).Path
# Relative to the PowerShell location (not the process directory, which differs after Set-Location).
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
if ($Compose) { $Compose = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Compose) }
if ($Service -and -not $Compose) { Fail '-Service needs -Compose <file>.' 2 }
if ((Test-Path -LiteralPath $Destination) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count -gt 0) {
    Fail "The destination folder is not empty: $Destination. Choose a new or empty folder." 2
}

# Best effort: refuse a live instance. A snapshot has no database journal written in the last minutes, and no
# program on this PC holds its database open. (A folder mounted from the server cannot be checked fully: stop
# 0.4.5 first or use a downloaded backup.)
if (Test-Path -LiteralPath $Source -PathType Container) {
    $recent = Get-ChildItem -LiteralPath $Source -Recurse -Depth 3 -File -Include '*.sqlite-wal', '*.sqlite-journal' -ErrorAction SilentlyContinue |
        Where-Object { $_.Length -gt 0 -and $_.LastWriteTimeUtc -gt (Get-Date).ToUniversalTime().AddMinutes(-10) }
    if ($recent) { Fail "$($recent[0].Name) was written less than 10 minutes ago: this looks like a running instance. Stop 0.4.5 (or download a backup) and use a copy." 2 }
    if ($Source -match '^\\\\wsl(\$|\.localhost)\\docker-desktop') { Fail 'This is a live Docker volume. Copy the folders first.' 2 }
}

# AUTOSOURCARR_DOTNET lets the wrapper test replace dotnet with a stub; normally it is the dotnet on PATH.
$dotnet = if ($env:AUTOSOURCARR_DOTNET) { $env:AUTOSOURCARR_DOTNET } else { 'dotnet' }
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET 10 SDK (dotnet) is needed: https://dotnet.microsoft.com/download' 2 }
$stale = -not (Test-Path -LiteralPath $dll)
if (-not $stale) {
    $built = (Get-Item -LiteralPath $dll).LastWriteTimeUtc
    $stale = [bool](Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Recurse -File -Include '*.cs', '*.csproj', '*.json', '*.sql' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.LastWriteTimeUtc -gt $built } | Select-Object -First 1)
}
if ($stale) {
    Write-Host 'Building AutoSourcarr (Release)...'
    & $dotnet restore --locked-mode $project
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet restore failed. DO NOT DEPLOY anything; nothing was migrated.' 3 }
    & $dotnet build -c Release --no-restore $project
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet build failed. DO NOT DEPLOY anything; nothing was migrated.' 3 }
}

$toolArgs = @('--migrate-0.4', $Source, $Destination)
if ($Compose) { $toolArgs += @('--compose', $Compose) }
if ($Service) { $toolArgs += @('--service', $Service) }
& $dotnet $dll @toolArgs
$code = $LASTEXITCODE
$report = Join-Path $Destination 'parity-report.md'
Write-Host ''
if (Test-Path -LiteralPath $report) {
    $verdict = Select-String -LiteralPath $report -Pattern '^\*\*(PARITY|MIGRATION) ' | Select-Object -First 1
    if ($verdict) { Write-Host ($verdict.Line.Trim('*')) }
    Write-Host "Report:  $report"
}
if ($code -eq 0) {
    Write-Host "Output:  $Destination (data\ -> /data, config\ -> /config)"
    Write-Host 'Next: read the report, then follow docs/upgrade-from-0.4.md (deploy only after your go).'
} else {
    Write-Host 'DO NOT DEPLOY this output. Read the report (or MIGRATION-INCOMPLETE.txt); your 0.4.x data is unchanged.'
}
exit $code
