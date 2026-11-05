[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory,[switch]$AllowEphemeralCiTrust,[switch]$RejectionOnly)
$ErrorActionPreference='Stop'
if (($AllowEphemeralCiTrust.IsPresent -eq $RejectionOnly.IsPresent) -or $env:GITHUB_ACTIONS -cne 'true') {
    throw 'Signing integration test is restricted to an explicitly approved disposable GitHub Actions runner.'
}
$helper=Join-Path $PSScriptRoot '..\Setup\Sign-ScomEvaluationPackage.ps1'
$root=Join-Path ([IO.Path]::GetTempPath()) ('scom-sign-test-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$source=[IO.Path]::GetFullPath($PackageDirectory)
$manifestPath=Join-Path $source 'package-manifest.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$hash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$cert=$null
function Reject {
    param([hashtable]$Arguments)
    $rejected=$false
    try { & $helper @Arguments | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Signing rejection test unexpectedly succeeded.' }
}
try {
    Write-Output 'Signing CI: validating rejection paths before any certificate creation.'
    $arguments=@{PackageDirectory=$source;DestinationDirectory=(Join-Path $root 'signed')
        ExpectedManifestSha256=$hash;ExpectedSourceCommit=$manifest.sourceCommit}
    $bad=$arguments.Clone();$bad.ExpectedManifestSha256='0'*64;Reject $bad
    $bad=$arguments.Clone();$bad.DestinationDirectory=Join-Path $source 'nested';Reject $bad
    $bad=$arguments.Clone();$bad.DestinationDirectory=$root;Reject $bad
    $bad=$arguments.Clone();$bad.CertificateThumbprint='0'*40;Reject $bad
    Write-Output 'Signing CI: missing signer rejected; validating file-set and digest tampering.'
    $changed=Join-Path $root 'changed'
    Copy-Item -LiteralPath $source -Destination $changed -Recurse
    $bad=$arguments.Clone();$bad.PackageDirectory=$changed
    [IO.File]::WriteAllText((Join-Path $changed 'unexpected.txt'),'synthetic')
    Reject $bad
    Remove-Item -LiteralPath (Join-Path $changed 'unexpected.txt')
    [IO.File]::AppendAllText((Join-Path $changed 'README.md'),'synthetic tamper')
    Reject $bad
    if (Test-Path -LiteralPath $arguments.DestinationDirectory) { throw 'Rejected signing mutated destination.' }
    if ($RejectionOnly) {
        Write-Output 'PASS: original manifest pins, overlap/existing outputs, missing signer, file-set and digest rejection. No certificate, trust, signing or policy changes; successful signed-derivative validation was NOT performed.'
        return
    }
    Write-Output 'Signing CI: creating nonexportable disposable software Code Signing key.'
    $cert=New-SelfSignedCertificate -Type CodeSigningCert -Subject ('CN=SCOM disposable CI '+[guid]::NewGuid().ToString('N')) `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1) `
        -Provider 'Microsoft Software Key Storage Provider' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256
    Write-Output 'Signing CI: disposable key created; rejecting signer without existing trust.'
    $arguments.CertificateThumbprint=$cert.Thumbprint
    Reject $arguments
    if (Test-Path -LiteralPath ($arguments.DestinationDirectory+'.partial')) { throw 'Untrusted signer created partial output.' }
    Write-Output 'Signing CI: adding only disposable CurrentUser trust in the isolated runner.'
    foreach ($name in @('Root','TrustedPublisher')) {
        $store=[Security.Cryptography.X509Certificates.X509Store]::new($name,'CurrentUser')
        try { $store.Open('ReadWrite');$store.Add($cert) } finally { $store.Close() }
    }
    $failure=$arguments.Clone();$failure.DestinationDirectory=Join-Path $root 'failed-signing'
    function Set-AuthenticodeSignature { throw 'Synthetic signing failure; no key used.' }
    try { Reject $failure } finally { Remove-Item -LiteralPath Function:\Set-AuthenticodeSignature }
    if (-not (Test-Path -LiteralPath ($failure.DestinationDirectory+'.partial')) -or
        (Test-Path -LiteralPath ($failure.DestinationDirectory+'.zip')) -or
        (Test-Path -LiteralPath ($failure.DestinationDirectory+'.zip.sha256'))) {
        throw 'Partial signing failure published a success artifact or lost partial evidence.'
    }
    Write-Output 'Signing CI: signing and verifying application-owned targets with disposable CurrentUser key.'
    $proof=& $helper @arguments
    if ($proof.status -cne 'VerifiedLocalSignedDerivative' -or $proof.signerThumbprint -ine $cert.Thumbprint -or
        $proof.signedFiles -lt 10 -or $proof.trustScope -cne 'CurrentUserOnly') { throw 'Signed derivative proof invalid.' }
    $derivative=Get-Content -LiteralPath (Join-Path $arguments.DestinationDirectory 'package-manifest.json') -Raw | ConvertFrom-Json
    if ($derivative.localSigning.originalManifestSha256 -cne $hash -or
        $derivative.localSigning.signerThumbprint -ine $cert.Thumbprint -or
        (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $hash) { throw 'Signing provenance or original preservation failed.' }
    & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackageDirectory $arguments.DestinationDirectory
    Reject $arguments
    Write-Output 'PASS: original manifest pins, overlap/existing outputs, missing signer/trust rejection, actual SHA256 EXE/script signing, archive revalidation, derivative provenance and original preservation. Disposable CI CurrentUser certificate/trust only.'
} finally {
    Write-Output 'Signing CI: removing only the disposable certificate/trust and named test directory.'
    if ($null -ne $cert) {
        foreach ($name in @('TrustedPublisher','Root')) {
            $store=[Security.Cryptography.X509Certificates.X509Store]::new($name,'CurrentUser')
            try { $store.Open('ReadWrite');$store.Remove($cert) } finally { $store.Close() }
        }
        Remove-Item -LiteralPath ('Cert:\CurrentUser\My\'+$cert.Thumbprint) -DeleteKey -Force
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
