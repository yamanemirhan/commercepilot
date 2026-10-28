$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
function New-LocalPassword {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
}
if (-not (Test-Path -LiteralPath '.env')) {
    Set-Content -LiteralPath '.env' -Value "POSTGRES_PASSWORD=$(New-LocalPassword)" -Encoding ascii
    Write-Host 'Created local database configuration.'
}
if (-not (Select-String -LiteralPath '.env' -Pattern '^AGENT_SHARED_KEY=' -Quiet)) {
    Add-Content -LiteralPath '.env' -Value "AGENT_SHARED_KEY=$(New-LocalPassword)" -Encoding ascii
    Write-Host 'Created the internal chat agent key.'
}
foreach ($account in @('ALEX', 'SAM', 'ADMIN')) {
    $key = "DEMO_${account}_PASSWORD"
    if (-not (Select-String -LiteralPath '.env' -Pattern "^$key=" -Quiet)) {
        Add-Content -LiteralPath '.env' -Value "$key=Cp1!$(New-LocalPassword)" -Encoding ascii
        Write-Host "Created a local password for $($account.ToLowerInvariant())@commercepilot.local."
    }
}
docker compose up --build -d
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed. Check that Docker Desktop is running with Linux containers.' }
$ready = $false
for ($attempt = 0; $attempt -lt 45; $attempt++) {
    try {
        $api = Invoke-WebRequest 'http://localhost:5080/health/ready' -UseBasicParsing -TimeoutSec 3
        $web = Invoke-WebRequest 'http://localhost:3000' -UseBasicParsing -TimeoutSec 3
        if ($api.StatusCode -eq 200 -and $web.StatusCode -eq 200) { $ready = $true; break }
    } catch { Start-Sleep -Seconds 2 }
}
if (-not $ready) { throw 'Services did not become ready. Run: docker compose logs api web' }
Write-Host 'CommercePilot is ready at http://localhost:3000'
Write-Host 'To view your local demo sign-in details, run: ./scripts/demo-accounts.ps1'
