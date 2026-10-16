param(
    [ValidateSet('review', 'validate', 'publish', 'status', 'query')][string]$Command = 'validate',
    [string]$Value = ''
)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$previousConnection = $env:ConnectionStrings__Commerce
try {
    if (!$env:ConnectionStrings__Commerce -and (Test-Path .env)) {
        $line = Get-Content .env | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
        if ($line) { $env:ConnectionStrings__Commerce = "Host=127.0.0.1;Port=5438;Database=commercepilot;Username=commercepilot;Password=$($line.Substring(18))" }
    }
    dotnet run --project tools/Commerce.Knowledge -- $Command $Value
    if ($LASTEXITCODE -ne 0) { throw 'Knowledge operation failed. See the diagnostic above.' }
} finally { $env:ConnectionStrings__Commerce = $previousConnection }
