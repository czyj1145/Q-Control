# =============================================================================
#  sign.ps1 - Apply an Authenticode signature to QControl binaries.
#
#  Uses the built-in Set-AuthenticodeSignature cmdlet, so it needs NO Windows
#  SDK / signtool.exe. It signs every .exe and .dll under the given folder
#  (recursively), skipping files that are already signed or that are not PE.
#
#  Usage:
#    powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 <folder>
#    powershell -ExecutionPolicy Bypass -File scripts\sign.ps1 <folder> -Thumbprint <SHA1>
#
#  By default it reads the cert from scripts\qcontrol.pfx (password in
#  scripts\pfx-password.txt, created by make-cert.ps1). Pass -Thumbprint to
#  sign with a cert already in CurrentUser\My instead.
#
#  Exit codes:
#    0  success (or nothing to do)
#    1  missing certificate / usage error
#    2  at least one file failed to sign
# =============================================================================
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Folder,

    [string]$Thumbprint = "",

    [string]$PfxPath = "",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------------------------------------------------------------------------
# Resolve the signing certificate into a $cert object.
# ---------------------------------------------------------------------------
$cert = $null

if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
    $cert = Get-ChildItem "Cert:\CurrentUser\My" | Where-Object { $_.Thumbprint -ieq $Thumbprint } | Select-Object -First 1
    if ($null -eq $cert) {
        Write-Error "No certificate in CurrentUser\My with thumbprint $Thumbprint. Run scripts\make-cert.ps1 first."
        exit 1
    }
}
else {
    if ([string]::IsNullOrWhiteSpace($PfxPath)) {
        $PfxPath = Join-Path $ScriptDir "qcontrol.pfx"
    }
    if (-not (Test-Path $PfxPath)) {
        Write-Warning "No PFX at $PfxPath. Run scripts\make-cert.ps1 to create a self-signed cert, then re-run."
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($Password)) {
        $PasswordFile = Join-Path $ScriptDir "pfx-password.txt"
        if (Test-Path $PasswordFile) {
            $Password = (Get-Content -Path $PasswordFile -Raw -Encoding ASCII).Trim()
        }
    }
    if ([string]::IsNullOrWhiteSpace($Password)) {
        Write-Error "No password for $PfxPath (expected at scripts\pfx-password.txt)."
        exit 1
    }
    $secure = ConvertTo-SecureString -String $Password -Force -AsPlainText
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $secure)

    # Self-signed certs verify as "Valid" only if the chain reaches a trusted
    # root. Ensure the cert is trusted in CurrentUser\Root so the signature
    # checks out cleanly (and Set-AuthenticodeSignature stops complaining).
    $root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'CurrentUser')
    $root.Open('ReadWrite')
    if ($null -eq ($root.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint })) {
        $root.Add($cert)
    }
    $root.Close()
}

# ---------------------------------------------------------------------------
# Sign every PE under the folder.
# ---------------------------------------------------------------------------
if (-not (Test-Path $Folder)) {
    Write-Error "Folder not found: $Folder"
    exit 1
}

$files = Get-ChildItem -Path $Folder -Recurse -File -Include *.exe, *.dll -ErrorAction SilentlyContinue
if ($null -eq $files -or $files.Count -eq 0) {
    Write-Host "No binaries under $Folder."
    exit 0
}

$failed = 0
$signed = 0
$skipped = 0

foreach ($f in $files) {
    # Skip files that already carry a signature (idempotent re-runs).
    $existing = Get-AuthenticodeSignature -FilePath $f.FullName -ErrorAction SilentlyContinue
    if ($null -ne $existing -and $existing.Status -eq "Valid") {
        $skipped++
        continue
    }

    # 先带时间戳签；拿不到时间戳（0x80072efd = 连不上服务器，国内常见）就降级重签一次。
    # 理由：这是自签证书，本来就不在信任链里，时间戳只决定"证书过期后签名还算不算数"，
    # 对我们没有意义 —— 别让一个网络问题把整条出包链挡住。
    $result = $null
    $firstErr = $null
    try {
        $result = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256
        if ($result.Status -ne "Valid") { $firstErr = $result.StatusMessage }
    }
    catch {
        $firstErr = $_.Exception.Message
    }

    if ($null -ne $result -and $result.Status -eq "Valid") {
        $signed++
        Write-Host ("  signed  {0}" -f $f.Name)
        continue
    }

    try {
        $result = Set-AuthenticodeSignature -FilePath $f.FullName -Certificate $cert -HashAlgorithm SHA256
    }
    catch {
        $failed++
        Write-Warning ("  ERROR   {0}: {1}" -f $f.Name, $_.Exception.Message)
        continue
    }

    if ($result.Status -eq "Valid") {
        $signed++
        Write-Host ("  signed  {0}  (无时间戳：{1})" -f $f.Name, $firstErr)
    }
    else {
        $failed++
        Write-Warning ("  FAILED  {0}: {1} / 重签仍不通过：{2}" -f $f.Name, $firstErr, $result.StatusMessage)
    }
}

Write-Host ""
Write-Host ("  Signed: {0}  Skipped: {1}  Failed: {2}" -f $signed, $skipped, $failed)
if ($failed -gt 0) { exit 2 }
exit 0
