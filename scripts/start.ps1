$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path -LiteralPath '.env')) {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $password = [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    Set-Content -LiteralPath '.env' -Value "POSTGRES_PASSWORD=$password" -Encoding ascii
    Write-Host 'Created local database configuration.'
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
