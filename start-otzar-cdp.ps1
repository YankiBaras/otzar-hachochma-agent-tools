# IT-741: start Otzar HaChochma with the Chromium debugging port, for AI agents.
# Touches no Otzar file and does not change the original desktop shortcut.
# The port is bound by Chromium to 127.0.0.1 only. --remote-allow-origins is deliberately NOT passed.
param(
    [int]$Port = 9222,
    [string]$AppDir = 'C:\OtzarApp\OtzarLocal\launcher\bin\x64\app'
)

$exe = Join-Path $AppDir 'otzar.exe'
if (-not (Test-Path $exe)) { Write-Error "otzar.exe not found at $exe"; exit 1 }

function Test-Cdp {
    try { Invoke-RestMethod "http://127.0.0.1:$Port/json/version" -TimeoutSec 2 | Out-Null; return $true } catch { return $false }
}

if (Test-Cdp) { Write-Host "Otzar CDP already available on 127.0.0.1:$Port"; exit 0 }

if (Get-Process -Name otzar -ErrorAction SilentlyContinue) {
    Write-Warning "Otzar is already running without the debugging port. Close it yourself and run this script again (nothing was stopped)."
    exit 2
}

Start-Process -FilePath $exe -WorkingDirectory $AppDir -ArgumentList "--remote-debugging-port=$Port"

for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Cdp) { Write-Host "Otzar started. CDP on http://127.0.0.1:$Port"; exit 0 }
}
Write-Error "Otzar started but CDP did not answer on port $Port within 60s"
exit 3
