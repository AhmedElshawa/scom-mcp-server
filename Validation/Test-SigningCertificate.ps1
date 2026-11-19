[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$helper = Join-Path $PSScriptRoot '..\Setup\Sign-ScomEvaluationPackage.ps1'
$source = [IO.Path]::GetFullPath($PackageDirectory)
$manifestPath = Join-Path $source 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$root = Join-Path ([IO.Path]::GetTempPath()) ('scom-sign-cert-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$thumbprint = '0' * 40
$arguments = @{
    PackageDirectory=$source; DestinationDirectory=(Join-Path $root 'output')
    ExpectedManifestSha256=$hash; ExpectedSourceCommit=$manifest.sourceCommit
    CertificateThumbprint=$thumbprint
}

function Get-Item {
    [CmdletBinding()]
    param([string]$LiteralPath,[switch]$Force)
    if ($LiteralPath -ceq ('Cert:\CurrentUser\My\' + $thumbprint)) {
        return $fixtureCertificate
    }
    Microsoft.PowerShell.Management\Get-Item @PSBoundParameters
}

function Assert-Rejection {
    param([string]$Expected)
    $rejected = $false
    try { & $helper @arguments | Out-Null } catch {
        if ($_.Exception.Message -cne $Expected) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Expected signing rejection: $Expected" }
    if ((Test-Path -LiteralPath $arguments.DestinationDirectory) -or
        (Test-Path -LiteralPath ($arguments.DestinationDirectory + '.partial'))) {
        throw 'Certificate validation created signing output.'
    }
}

try {
    $now = Get-Date
    $fixtureCertificate = [pscustomobject]@{
        HasPrivateKey=$true; NotBefore=$now.AddDays(-1); NotAfter=$now.AddDays(1)
        EnhancedKeyUsageList=@([pscustomobject]@{FriendlyName='Code Signing'; ObjectId='1.3.6.1.5.5.7.3.3'})
    }
    # A provider-shaped eligible certificate reaches, but cannot bypass, the existing trust gate.
    Assert-Rejection 'SIGN_CURRENT_USER_TRUST_REQUIRED_NO_TRUST_CHANGES_PERFORMED'
    $fixtureCertificate.EnhancedKeyUsageList=@(
        [pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.3'},
        [pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.1'}
    )
    Assert-Rejection 'SIGN_CURRENT_USER_TRUST_REQUIRED_NO_TRUST_CHANGES_PERFORMED'
    foreach ($case in @('wrongEku','similarEku','noEku','duplicateEku','noPrivateKey','expired','notYetValid')) {
        $fixtureCertificate.HasPrivateKey=$true
        $fixtureCertificate.NotBefore=$now.AddDays(-1)
        $fixtureCertificate.NotAfter=$now.AddDays(1)
        $fixtureCertificate.EnhancedKeyUsageList=@([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.3'})
        switch ($case) {
            'wrongEku' { $fixtureCertificate.EnhancedKeyUsageList=@([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.1'}) }
            'similarEku' { $fixtureCertificate.EnhancedKeyUsageList=@([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.30'}) }
            'noEku' { $fixtureCertificate.EnhancedKeyUsageList=@() }
            'duplicateEku' { $fixtureCertificate.EnhancedKeyUsageList=@([pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.3'},[pscustomobject]@{ObjectId='1.3.6.1.5.5.7.3.3'}) }
            'noPrivateKey' { $fixtureCertificate.HasPrivateKey=$false }
            'expired' { $fixtureCertificate.NotAfter=$now.AddDays(-1) }
            'notYetValid' { $fixtureCertificate.NotBefore=$now.AddDays(1) }
        }
        Assert-Rejection 'SIGN_CERTIFICATE_KEY_VALIDITY_OR_EKU_INVALID'
    }
    Write-Output 'PASS: provider-shaped code-signing EKU reaches the unchanged trust gate; invalid EKU, key and validity are rejected. No certificate or trust was changed.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
