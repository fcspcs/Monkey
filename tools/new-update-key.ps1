#requires -Version 5.1
#
# Erzeugt das Schluesselpaar fuer signierte Auto-Updates - einmalig, vom
# Projektbetreiber (bzw. vom Betreiber eines Forks) auszufuehren:
#
#   powershell -ExecutionPolicy Bypass -File tools/new-update-key.ps1
#
# Danach:
#   1. assets/update-key.pem einchecken (der OEFFENTLICHE Schluessel - er wird
#      beim Bauen in den Dienst eingebettet und prueft kuenftige Updates).
#   2. Den Inhalt der privaten Datei als GitHub-Actions-Secret
#      UPDATE_SIGNING_KEY hinterlegen:
#      Get-Content -Raw <Datei> | gh secret set UPDATE_SIGNING_KEY
#      (oder im Browser: Settings > Secrets and variables > Actions).
#   3. Die private Datei an einen sicheren Ort verschieben und NIE einchecken -
#      sie steht bereits in der .gitignore.
#
# Wichtig: Wer den oeffentlichen Schluessel spaeter austauscht, koppelt alle
# bereits installierten Fassungen vom Auto-Update ab - die kennen nur den alten.
#
[CmdletBinding()]
param(
    [string]$PrivateKeyPath = "",
    [string]$PublicKeyPath  = ""
)

$ErrorActionPreference = 'Stop'

if (-not $PrivateKeyPath) {
    $PrivateKeyPath = Join-Path (Split-Path -Parent $PSScriptRoot) "update-key.private.pem"
}
if (-not $PublicKeyPath) {
    $PublicKeyPath = Join-Path (Split-Path -Parent $PSScriptRoot) "assets\update-key.pem"
}

# Dasselbe benutzerlokale SDK wie build.ps1 verwenden. So funktioniert die
# einmalige Schluesselerzeugung auch auf Windows PowerShell 5.1; moderne PEM-
# Exporte kommen aus .NET 8 statt aus der alten PowerShell-Laufzeit.
$localDotnet = Join-Path $env:USERPROFILE ".dotnet"
if (Test-Path (Join-Path $localDotnet "dotnet.exe")) {
    $env:PATH = "$localDotnet;$env:PATH"
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Get the .NET 8 SDK from https://dot.net"
}

if (Test-Path $PublicKeyPath) {
    throw "There is already a public key at '$PublicKeyPath'. Replacing it orphans every installed copy - delete it on purpose first if you really mean to."
}
if (Test-Path $PrivateKeyPath) {
    throw "There is already a private key at '$PrivateKeyPath'. Move it somewhere safe first."
}

$keyTool = Join-Path $PSScriptRoot "UpdateKeyTool\UpdateKeyTool.csproj"
dotnet run --project $keyTool --configuration Release -- $PrivateKeyPath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "Update key generation failed." }
dotnet run --project $keyTool --configuration Release -- verify $PrivateKeyPath $PublicKeyPath
if ($LASTEXITCODE -ne 0) { throw "The generated update key pair failed its signature self-test." }

# Die Datei ist git-ignoriert; unter Windows zusaetzlich nur den aktuellen
# Benutzer und SYSTEM lesen lassen. Das ersetzt kein externes Backup, begrenzt
# aber die versehentliche lokale Offenlegung bis dorthin.
try {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $system = [System.Security.Principal.SecurityIdentifier]::new(
        [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
    $acl = Get-Acl -LiteralPath $PrivateKeyPath
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { $acl.RemoveAccessRuleSpecific($rule) }
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $identity.User, 'FullControl', 'Allow'))
    $acl.AddAccessRule([System.Security.AccessControl.FileSystemAccessRule]::new(
        $system, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $PrivateKeyPath -AclObject $acl
} catch {
    Write-Warning "Could not restrict the private key ACL: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "Key pair created (ECDSA P-256)." -ForegroundColor Green
Write-Host ""
Write-Host "  Public  key: $PublicKeyPath   -> commit this file"
Write-Host "  Private key: $PrivateKeyPath  -> NEVER commit; store as the"
Write-Host "               GitHub Actions secret UPDATE_SIGNING_KEY, e.g.:"
Write-Host ""
Write-Host "                 Get-Content -Raw `"$PrivateKeyPath`" | gh secret set UPDATE_SIGNING_KEY" -ForegroundColor Yellow
Write-Host ""
Write-Host "               then move the file somewhere safe (password manager,"
Write-Host "               offline drive) and delete it here."
