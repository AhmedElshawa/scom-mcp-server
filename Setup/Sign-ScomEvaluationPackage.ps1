[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{40}$')][string]$ExpectedSourceCommit,
    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint='FD229CD0460526F7A1BDE7AF70C12670C9904B59'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$thumb=$CertificateThumbprint.ToUpperInvariant()
$source=[IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
$destination=[IO.Path]::GetFullPath($DestinationDirectory).TrimEnd('\')
$partial=$destination+'.partial'
$zip=$destination+'.zip'
$zipPartial=$zip+'.partial'
$sidecar=$zip+'.sha256'
$verification=$destination+'.verify'
function Assert-NoReparse {
    param([string]$Path)
    $item=Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'SIGN_REPARSE_POINT_REJECTED' }
        $item=$item.Parent
    }
}
function Get-Digest { param([string]$Path) (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Assert-Manifest {
    param([string]$Root,$Manifest)
    $files=@(Get-ChildItem -LiteralPath $Root -File -Recurse -Force)
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.sourceCommit -cne $ExpectedSourceCommit -or
        $Manifest.certificationStatus -cne 'notCertified' -or
        $Manifest.files.Count -ne ($files.Count-1)) { throw 'SIGN_MANIFEST_IDENTITY_OR_FILE_SET_INVALID' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Manifest.files) {
        if (-not $seen.Add([string]$entry.path) -or [IO.Path]::IsPathRooted($entry.path) -or
            $entry.path -match '(^|[\\/])\.\.?([\\/]|$)|:|/' -or
            $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'SIGN_MANIFEST_PATH_INVALID' }
        $path=[IO.Path]::GetFullPath((Join-Path $Root $entry.path))
        if (-not $path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Digest $path) -ine $entry.sha256) { throw 'SIGN_MANIFEST_DIGEST_MISMATCH' }
    }
    if ($seen.Contains('package-manifest.json')) { throw 'SIGN_SELF_REFERENTIAL_MANIFEST' }
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw 'SIGN_SOURCE_MISSING' }
if ($destination -ieq $source -or $destination.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $source.StartsWith($destination+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'SIGN_OUTPUT_OVERLAPS_SOURCE' }
foreach ($path in @($destination,$partial,$zip,$zipPartial,$sidecar,$sidecar+'.partial',$verification)) {
    if (Test-Path -LiteralPath $path) { throw 'SIGN_OUTPUT_ALREADY_EXISTS' }
}
$parent=Split-Path $destination -Parent
if (-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'SIGN_EXISTING_OUTPUT_PARENT_REQUIRED' }
Assert-NoReparse $parent
Assert-NoReparse $source
foreach ($item in Get-ChildItem -LiteralPath $source -Force -Recurse) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'SIGN_REPARSE_POINT_REJECTED' }
}
$manifestPath=Join-Path $source 'package-manifest.json'
if ((Get-Digest $manifestPath) -ine $ExpectedManifestSha256) { throw 'SIGN_REVIEWED_MANIFEST_HASH_MISMATCH' }
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.PSObject.Properties['localSigning']) { throw 'SIGN_ORIGINAL_REVIEWED_PACKAGE_REQUIRED' }
Assert-Manifest $source $manifest
foreach ($entry in $manifest.files) {
    if ($entry.path -match '(^|\\)(appsettings\.json|worker\.json|setup-plan\.json|installed-files\.json|setup-state\.json)$' -or
        $entry.path -match '\.(pfx|p12|key|db|bak)$') { throw 'SIGN_RUNTIME_STATE_OR_SECRET_REJECTED' }
}
$worker=Get-Content -LiteralPath (Join-Path $source 'server\appsettings.Worker.example.json') -Raw | ConvertFrom-Json
if ($worker.profile.scriptSha256 -cne 'REPLACE_WITH_REVIEWED_INSTALLED_SCRIPT_SHA256') {
    throw 'SIGN_CONFIGURED_PROFILE_REJECTED'
}
$targets=@($manifest.files | Where-Object {
    $_.path -cin @('ScomSetup.exe','server\ScomMcp.exe') -or $_.path -match '\.(ps1|psm1)$'
})
if (@($targets | Where-Object path -ceq 'ScomSetup.exe').Count -ne 1 -or
    @($targets | Where-Object path -ceq 'setup\Invoke-ScomSetupGui.ps1').Count -ne 1) { throw 'SIGN_REQUIRED_TARGET_MISSING' }
foreach ($entry in $targets) {
    if ($entry.path -match '\.(ps1|psm1)$' -and
        $entry.path -notmatch '^(setup|collector|Operations\\DurableIndexedRefresh)\\') { throw 'SIGN_UNEXPECTED_SCRIPT_TARGET' }
}
$cert=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$thumb)
$now=Get-Date
if (-not $cert.HasPrivateKey -or $cert.NotBefore -gt $now -or $cert.NotAfter -le $now -or
    @($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -ceq '1.3.6.1.5.5.7.3.3' }).Count -ne 1) {
    throw 'SIGN_CERTIFICATE_KEY_VALIDITY_OR_EKU_INVALID'
}
foreach ($storeName in @('Root','TrustedPublisher')) {
    $store=[Security.Cryptography.X509Certificates.X509Store]::new($storeName,'CurrentUser')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        if (@($store.Certificates | Where-Object Thumbprint -eq $thumb).Count -ne 1) {
            throw 'SIGN_CURRENT_USER_TRUST_REQUIRED_NO_TRUST_CHANGES_PERFORMED'
        }
    } finally { $store.Close() }
}
$null=New-Item -ItemType Directory -Path $partial
$publishedZip=$false
$publishedSidecar=$false
try {
    foreach ($entry in $manifest.files) {
        $target=Join-Path $partial $entry.path
        $folder=Split-Path $target -Parent
        if (-not (Test-Path -LiteralPath $folder)) { $null=New-Item -ItemType Directory -Path $folder -Force }
        Copy-Item -LiteralPath (Join-Path $source $entry.path) -Destination $target
        if ((Get-Digest $target) -ine $entry.sha256) { throw 'SIGN_COPY_DIGEST_MISMATCH' }
    }
    foreach ($entry in $targets) {
        $path=Join-Path $partial $entry.path
        $result=Set-AuthenticodeSignature -LiteralPath $path -Certificate $cert -HashAlgorithm SHA256
        $verified=Get-AuthenticodeSignature -LiteralPath $path
        if ($result.Status -ne 'Valid' -or $verified.Status -ne 'Valid' -or
            $verified.SignerCertificate.Thumbprint -ine $thumb) { throw 'SIGN_SIGNATURE_VERIFICATION_FAILED' }
    }
    # Untouched vendor/runtime files must remain identical to the reviewed original.
    $signedPaths=@($targets | ForEach-Object path)
    foreach ($entry in $manifest.files | Where-Object { $_.path -cnotin $signedPaths }) {
        if ((Get-Digest (Join-Path $partial $entry.path)) -ine $entry.sha256) { throw 'SIGN_UNINTENDED_FILE_CHANGE' }
    }
    if ((Get-Digest $manifestPath) -ine $ExpectedManifestSha256) { throw 'SIGN_SOURCE_CHANGED_DURING_OPERATION' }
    Assert-Manifest $source $manifest
    $derivative=[ordered]@{
        schemaVersion=1;sourceCommit=$manifest.sourceCommit;certificationStatus='notCertified'
        localSigning=[ordered]@{
            kind='currentUserLabSignedDerivative';originalManifestSha256=$ExpectedManifestSha256.ToUpperInvariant()
            signerThumbprint=$thumb;algorithm='SHA256';timestamped=$false
            trustScope='CurrentUserOnly';signedAtUtc=[DateTime]::UtcNow.ToString('o');signedFiles=$signedPaths
        }
        files=@(Get-ChildItem -LiteralPath $partial -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{path=$_.FullName.Substring($partial.Length+1);sha256=(Get-Digest $_.FullName)}
        })
    }
    [IO.File]::WriteAllText((Join-Path $partial 'package-manifest.json'),
        ($derivative | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Assert-Manifest $partial $derivative
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($partial,$zipPartial)
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPartial,$verification)
    # Exact archived hashes include signature bytes already verified above.
    Assert-Manifest $verification $derivative
    Remove-Item -LiteralPath $verification -Recurse -Force
    $zipHash=Get-Digest $zipPartial
    [IO.File]::WriteAllText(($sidecar+'.partial'),($zipHash+'  '+[IO.Path]::GetFileName($zip)+"`r`n"),
        [Text.Encoding]::ASCII)
    Move-Item -LiteralPath $partial -Destination $destination
    Move-Item -LiteralPath $zipPartial -Destination $zip
    $publishedZip=$true
    Move-Item -LiteralPath ($sidecar+'.partial') -Destination $sidecar
    $publishedSidecar=$true
    [pscustomobject]@{status='VerifiedLocalSignedDerivative';sourceCommit=$manifest.sourceCommit
        signerThumbprint=$thumb;signedFiles=$targets.Count;zipSha256=$zipHash
        trustScope='CurrentUserOnly';timestamped=$false}
} catch {
    if ($publishedSidecar) { Remove-Item -LiteralPath $sidecar -Force }
    if ($publishedZip) { Remove-Item -LiteralPath $zip -Force }
    throw 'SIGN_DERIVATIVE_FAILED: No success is claimed. Preserve partial outputs for review; do not deploy them. Original package was not modified.'
}
