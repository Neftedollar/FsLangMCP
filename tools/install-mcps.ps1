# Install my-mcps — thin wrapper, delegates to multiagent-setup CLI
# Usage: .\tools\install-mcps.ps1 [options]
param([Parameter(ValueFromRemainingArguments)][string[]]$PassArgs)

$env:PATH += ";$env:USERPROFILE\.dotnet\tools"

if (-not (Get-Command multiagent-setup -ErrorAction SilentlyContinue)) {
    Write-Host "  ..  Installing multiagent-setup..."
    & dotnet tool install -g multiagent-setup 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { & dotnet tool update -g multiagent-setup }
}

& multiagent-setup install-mcps @PassArgs
exit $LASTEXITCODE
