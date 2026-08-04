#
# Baut den Installer nach .\dist. Ergebnis ist eine einzige Datei:
# TimeGuardSetup.exe - sie enthaelt Dienst und Agent als Nutzlast.
# Braucht keine erhoehten Rechte.
#
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Output = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"

# Das SDK liegt benutzerlokal, falls es nicht systemweit installiert ist.
$localDotnet = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) {
    $env:PATH = "$localDotnet;$env:PATH"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet wurde nicht gefunden. SDK holen von https://dot.net"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
$env:DOTNET_NOLOGO = 1

$staging = Join-Path $PSScriptRoot "build\staging"
$payload = Join-Path $PSScriptRoot "src\TimeGuard.Setup\payload"

function Reset-Dir($path) {
    if (Test-Path $path) { Get-ChildItem $path -Recurse -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    else { New-Item -ItemType Directory -Path $path | Out-Null }
}

Reset-Dir $Output
Reset-Dir $staging
Reset-Dir $payload

# 1. Dienst und Agent als je eine Einzeldatei-Exe erzeugen.
foreach ($app in @("TimeGuard.Service", "TimeGuard.Agent")) {
    Write-Host "Baue $app (Einzeldatei) ..." -ForegroundColor Cyan
    $csproj = Join-Path $PSScriptRoot "src\$app\$app.csproj"
    $out = Join-Path $staging $app
    dotnet publish $csproj -c $Configuration -o $out --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build von $app fehlgeschlagen." }
}

# 2. Die beiden Exe-Dateien als Nutzlast fuer den Installer bereitstellen.
Copy-Item (Join-Path $staging "TimeGuard.Service\TimeGuardService.exe") $payload -Force
Copy-Item (Join-Path $staging "TimeGuard.Agent\TimeGuardAgent.exe") $payload -Force

# 3. Den Installer bauen - er bettet die Nutzlast ein.
Write-Host "Baue TimeGuard.Setup (Einzeldatei mit eingebetteter Nutzlast) ..." -ForegroundColor Cyan
$setup = Join-Path $PSScriptRoot "src\TimeGuard.Setup\TimeGuard.Setup.csproj"
dotnet publish $setup -c $Configuration -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "Build von TimeGuard.Setup fehlgeschlagen." }

# 4. Aufraeumen: Nutzlast und Staging werden nicht mehr gebraucht.
Reset-Dir $payload; Remove-Item $payload -Force -ErrorAction SilentlyContinue
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

# Etwaige lose Begleitdateien aus dem Ausgabeordner entfernen - es soll nur die
# eine Setup-Datei uebrig bleiben.
Get-ChildItem $Output -File | Where-Object { $_.Name -ne "TimeGuardSetup.exe" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Fertig. Eine Datei:" -ForegroundColor Green
Get-ChildItem $Output -File | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host ""
Write-Host "Starten: dist\TimeGuardSetup.exe doppelklicken und die Windows-Abfrage bestaetigen." -ForegroundColor Yellow
