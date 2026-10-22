$ErrorActionPreference = 'Stop'
$configFile = Join-Path (Split-Path -Parent $PSScriptRoot) '.env'
if (-not (Test-Path -LiteralPath $configFile)) { throw 'Run ./scripts/start.ps1 first.' }
foreach ($name in @('ALEX', 'SAM', 'ADMIN')) {
    $key = "DEMO_${name}_PASSWORD"
    $line = Get-Content -LiteralPath $configFile | Where-Object { $_ -match "^$key=" } | Select-Object -First 1
    if (-not $line) { throw "Missing $key. Run ./scripts/start.ps1." }
    Write-Host "Email:    $($name.ToLowerInvariant())@commercepilot.local"
    Write-Host "Password: $($line.Substring($key.Length + 1))"
    Write-Host ''
}
Write-Host 'These credentials are local to this installation. Do not commit or share .env.'
