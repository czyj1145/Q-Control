# =============================================================================
#  make-cert.ps1 - Create a self-signed code-signing certificate for QControl.
#
#  What it does:
#    * Creates a code-signing cert in CurrentUser\My (name "QControl Dev").
#    * Exports it as a PFX so sign.ps1 (and CI) can use it without touching
#      the certificate store.
#
#  Why self-signed is "free but not clean":
#    An Authenticode signature removes the "unknown publisher" *warning* that
#    comes from a missing signature, and it survives once the user has
#    imported the cert. But Windows SmartScreen still does not trust a
#    self-signed cert out of the box - a download with a Mark-of-the-Web will
#    still show the SmartScreen prompt until enough users have run it OR the
#    cert is signed by a public CA. For local / small-circle distribution this
#    is enough. For wide public distribution, swap the cert for an OV/EV cert;
#    sign.ps1 does not care where the PFX came from.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\make-cert.ps1
#    powershell -ExecutionPolicy Bypass -File scripts\make-cert.ps1 -ExportPath .\scripts\qcontrol.pfx
#
#  The PFX password is stored in scripts\pfx-password.txt (gitignored).
# =============================================================================
param(
    [string]$CertName = "QControl Dev",
    [string]$ExportPath = "",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Resolve the scripts directory (this file's folder) for default output paths.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ExportPath)) {
    $ExportPath = Join-Path $ScriptDir "qcontrol.pfx"
}
$PasswordFile = Join-Path $ScriptDir "pfx-password.txt"

if ([string]::IsNullOrWhiteSpace($Password)) {
    # A throwaway password; the PFX stays local and gitignored. Storing the
    # plaintext password next to the PFX only makes sense for a self-signed
    # dev cert whose private key is not a real secret.
    $Password = -join ((48..57 + 65..90 + 97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
    Set-Content -Path $PasswordFile -Value $Password -NoNewline -Encoding ASCII
}

# Self-signed cert must live in the machine/current-user store before
# Set-AuthenticodeSignature can sign with it. Type CodeSigning is required.
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=$CertName, O=QControl, OU=Development" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyUsage DigitalSignature `
    -NotAfter (Get-Date).AddYears(10)

Write-Host ""
Write-Host "  Created code-signing certificate:"
Write-Host "    Subject : $($cert.Subject)"
Write-Host "    Thumb   : $($cert.Thumbprint)"
Write-Host "    Expires : $($cert.NotAfter)"
Write-Host ""

# Export the PFX (with private key) so it can be used outside this store.
$secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $ExportPath -Password $secure | Out-Null

# Trust the cert as a root in CurrentUser\Root. This is what makes
# Set-AuthenticodeSignature return "Valid" instead of "certificate chain
# terminated in a root that is not trusted". It also makes THIS machine
# treat the signature as trusted (no "unknown publisher" prompt).
$root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
$root.Open('ReadWrite')
if ($null -eq ($root.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
    $root.Add($cert)
}
$root.Close()

Write-Host "  Exported PFX -> $ExportPath"
Write-Host "  Password    -> $PasswordFile"
Write-Host "  Trusted in  -> CurrentUser\Root (so the signature verifies as Valid)"
Write-Host ""
Write-Host "  Next: build the project, then run scripts\sign.ps1 (or let the"
Write-Host "  automatic post-build signing in Directory.Build.targets pick it up)."
Write-Host ""
