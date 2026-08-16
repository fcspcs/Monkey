#
# Runs the whole test suite - the same checks the CI performs before it
# rebuilds the installer or publishes a release:
#
#   1. cloud/worker.test.mjs  - the Telegram relay Worker, over its real HTTP
#                               surface (needs Node.js 20+)
#   2. src/Monkey.Tests       - engine, state, protocol and the PC side of the
#                               Telegram sync against a local fake worker
#
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Das SDK liegt benutzerlokal, falls es nicht systemweit installiert ist.
$localDotnet = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) {
    $env:PATH = "$localDotnet;$env:PATH"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Get the SDK from https://dot.net"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1

Push-Location $PSScriptRoot
try {
    if (Get-Command node -ErrorAction SilentlyContinue) {
        Write-Host "Worker tests (cloud/worker.test.mjs) ..." -ForegroundColor Cyan
        node --check cloud/worker.js
        if ($LASTEXITCODE -ne 0) { throw "worker.js does not parse." }
        node cloud/worker.test.mjs
        if ($LASTEXITCODE -ne 0) { throw "Worker tests failed." }
    }
    else {
        Write-Warning "Node.js not found - skipping the Worker tests. CI still runs them."
    }

    Write-Host "Service tests (src/Monkey.Tests) ..." -ForegroundColor Cyan
    dotnet test src/Monkey.Tests/Monkey.Tests.csproj -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw ".NET tests failed." }

    Write-Host ""
    Write-Host "All tests passed." -ForegroundColor Green
}
finally {
    Pop-Location
}
