#requires -Version 7
#
# Erzeugt das Schluesselpaar fuer signierte Auto-Updates - einmalig, vom
# Projektbetreiber (bzw. vom Betreiber eines Forks) auszufuehren:
#
#   pwsh tools/new-update-key.ps1
#
# Danach:
#   1. assets/update-key.pem einchecken (der OEFFENTLICHE Schluessel - er wird
#      beim Bauen in den Dienst eingebettet und prueft kuenftige Updates).
#   2. Den Inhalt der privaten Datei als GitHub-Actions-Secret
#      UPDATE_SIGNING_KEY hinterlegen:  gh secret set UPDATE_SIGNING_KEY < <Datei>
#      (oder im Browser: Settings > Secrets and variables > Actions).
#   3. Die private Datei an einen sicheren Ort verschieben und NIE einchecken -
#      sie steht bereits in der .gitignore.
#
# Wichtig: Wer den oeffentlichen Schluessel spaeter austauscht, koppelt alle
# bereits installierten Fassungen vom Auto-Update ab - die kennen nur den alten.
#
[CmdletBinding()]
param(
    [string]$PrivateKeyPath = (Join-Path $PSScriptRoot ".." "update-key.private.pem"),
    [string]$PublicKeyPath  = (Join-Path $PSScriptRoot ".." "assets" "update-key.pem")
)

$ErrorActionPreference = 'Stop'

if (Test-Path $PublicKeyPath) {
    throw "There is already a public key at '$PublicKeyPath'. Replacing it orphans every installed copy - delete it on purpose first if you really mean to."
}
if (Test-Path $PrivateKeyPath) {
    throw "There is already a private key at '$PrivateKeyPath'. Move it somewhere safe first."
}

$ecdsa = [System.Security.Cryptography.ECDsa]::Create(
    [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)

Set-Content -Path $PrivateKeyPath -Value $ecdsa.ExportPkcs8PrivateKeyPem() -Encoding ascii -NoNewline
Set-Content -Path $PublicKeyPath  -Value $ecdsa.ExportSubjectPublicKeyInfoPem() -Encoding ascii -NoNewline

Write-Host ""
Write-Host "Key pair created (ECDSA P-256)." -ForegroundColor Green
Write-Host ""
Write-Host "  Public  key: $PublicKeyPath   -> commit this file"
Write-Host "  Private key: $PrivateKeyPath  -> NEVER commit; store as the"
Write-Host "               GitHub Actions secret UPDATE_SIGNING_KEY, e.g.:"
Write-Host ""
Write-Host "                 gh secret set UPDATE_SIGNING_KEY < `"$PrivateKeyPath`"" -ForegroundColor Yellow
Write-Host ""
Write-Host "               then move the file somewhere safe (password manager,"
Write-Host "               offline drive) and delete it here."
