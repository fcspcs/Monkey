#
# Builds the installer into .\dist. The result is a single file:
# MonkeySetup.exe - it carries the service and the display inside.
# No elevated rights needed.
#
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "dist"),

    # Ueberschreibt die Version aus Directory.Build.props - beim
    # Veroeffentlichen kommt sie aus dem Tag.
    [string]$Version = ""
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

$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

$staging = Join-Path $PSScriptRoot "build\staging"
$payload = Join-Path $PSScriptRoot "src\Monkey.Setup\payload"

function Reset-Dir($path) {
    if (Test-Path $path) { Get-ChildItem $path -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    else { New-Item -ItemType Directory -Path $path | Out-Null }
}

Reset-Dir $Output
Reset-Dir $staging
Reset-Dir $payload

# 1. Dienst und Agent als je eine Einzeldatei-Exe erzeugen.
foreach ($app in @("Monkey.Service", "Monkey.Agent")) {
    Write-Host "Building $app (single file) ..." -ForegroundColor Cyan
    $csproj = Join-Path $PSScriptRoot "src\$app\$app.csproj"
    $out = Join-Path $staging $app
    dotnet publish $csproj -c $Configuration -o $out --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Build of $app failed." }
}

# 2. Die beiden Exe-Dateien als Nutzlast fuer den Installer bereitstellen.
Copy-Item (Join-Path $staging "Monkey.Service\MonkeyService.exe") $payload -Force
Copy-Item (Join-Path $staging "Monkey.Agent\MonkeyAgent.exe") $payload -Force

# 3. Den Installer bauen - er bettet die Nutzlast ein.
Write-Host "Building Monkey.Setup (single file with embedded payload) ..." -ForegroundColor Cyan
$setup = Join-Path $PSScriptRoot "src\Monkey.Setup\Monkey.Setup.csproj"
dotnet publish $setup -c $Configuration -o $Output --nologo @versionArgs
if ($LASTEXITCODE -ne 0) { throw "Build of Monkey.Setup failed." }

# 4. Aufraeumen: Nutzlast und Staging werden nicht mehr gebraucht.
Reset-Dir $payload; Remove-Item $payload -Force -ErrorAction SilentlyContinue
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

# Etwaige lose Begleitdateien aus dem Ausgabeordner entfernen - es soll nur die
# eine Setup-Datei uebrig bleiben.
Get-ChildItem $Output -File | Where-Object { $_.Name -ne "MonkeySetup.exe" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. One file:" -ForegroundColor Green
Get-ChildItem $Output -File | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host ""
Write-Host "Run it: double-click dist\MonkeySetup.exe and accept the Windows prompt." -ForegroundColor Yellow
