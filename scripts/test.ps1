$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path -LiteralPath '.env')) { throw 'Run ./scripts/start.ps1 first.' }
$passwordLine = Get-Content -LiteralPath '.env' | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
if (-not $passwordLine) { throw 'POSTGRES_PASSWORD is missing from .env.' }
$databasePassword = $passwordLine.Substring('POSTGRES_PASSWORD='.Length)
$originalConnection = $env:TEST_DATABASE_CONNECTION
try {
    $env:TEST_DATABASE_CONNECTION = "Host=127.0.0.1;Port=5438;Database=postgres;Username=commercepilot;Password=$databasePassword"
    dotnet test tests/Commerce.IntegrationTests --configuration Release --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
} finally {
    $env:TEST_DATABASE_CONNECTION = $originalConnection
}
