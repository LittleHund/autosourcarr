param([ValidateRange(1,65535)][int]$Port = 8383)

$ErrorActionPreference = 'Stop'
$previewScript = Join-Path $PSScriptRoot 'scripts\preview-reimagined.py'
$previewPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
if (-not (Test-Path -LiteralPath $previewPython -PathType Leaf)) {
    $previewCommand = Get-Command python -ErrorAction SilentlyContinue
    if (-not $previewCommand) { throw 'Python 3 is needed for the optional local sample preview.' }
    $previewPython = $previewCommand.Source
}
& $previewPython -B $previewScript --port $Port
exit $LASTEXITCODE
