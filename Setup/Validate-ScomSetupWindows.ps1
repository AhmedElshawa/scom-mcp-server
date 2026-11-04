Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1') -Force
$module = Get-Module ScomSetup.Windows
$facts = [pscustomobject]@{
    osBuild=20348; productType=3; dotnet=$true; runtimeLines=@('Microsoft.NETCore.App 8.0.0 [synthetic]','Microsoft.AspNetCore.App 8.0.0 [synthetic]')
    iis=$true; iisServer=$true; ancm=$true; hostingBundle=$true; sdk=$true; sdkMetadata=$true; restart=$false; poolExists=$false; siteExists=$false
    osProbeFails=$false;registryProbeFails=$false;runtimeExit=0
}
# Substitute read-only command probes INSIDE the adapter module. No IIS
# commands, registry writes, runtime execution or source connection can occur.
& $module {
    param($fixture)
    $script:facts=$fixture
    function script:Get-CimInstance {
        if ($script:facts.osProbeFails) { throw 'Synthetic CIM access denied: private-marker' }
        [pscustomobject]@{BuildNumber=$script:facts.osBuild;ProductType=$script:facts.productType}
    }
    function script:Get-Command {
        if ($script:facts.dotnet) { [pscustomobject]@{Source='synthetic-dotnet'} }
    }
    function script:synthetic-dotnet {
        $global:LASTEXITCODE=$script:facts.runtimeExit
        $script:facts.runtimeLines
    }
    function script:Get-Module { if ($script:facts.iis) { [pscustomobject]@{Name='synthetic-IIS'} } }
    function script:Get-Service { if ($script:facts.iisServer) { [pscustomobject]@{Name='W3SVC'}; [pscustomobject]@{Name='WAS'} } }
    function script:Import-Module {}
    function script:Get-ScomSetupLocalScom {
        param($ModulePath)
        [pscustomobject]@{modulePath='C:\synthetic\OperationsManager.psd1';moduleValid=$script:facts.sdk;sdkValid=$script:facts.sdkMetadata;failureCode=$null}
    }
    function script:Get-ItemProperty {
        param($Path)
        if ($Path -match 'Uninstall') {
            if ($script:facts.registryProbeFails) { throw 'Synthetic registry access denied: private-marker' }
            if ($script:facts.hostingBundle) { [pscustomobject]@{DisplayName='Microsoft .NET 8.0.0 - Windows Server Hosting'} }
        } else { [pscustomobject]@{} }
    }
    function script:Test-Path {
        param($Path, $LiteralPath)
        $target = if ($LiteralPath) { $LiteralPath } else { $Path }
        if ($target -match '^IIS:') { return $script:facts.poolExists }
        if ($target -match 'RebootPending|RebootRequired') { return $script:facts.restart }
        if ($target -match 'aspnetcorev2') { return $script:facts.ancm }
        if ($target -match 'OperationsManager.psd1') { return $script:facts.sdk }
        throw 'Unexpected probe in synthetic Windows fixture'
    }
    function script:Get-Website {
        if ($script:facts.siteExists) { [pscustomobject]@{Name='synthetic-site'} }
    }
    function script:Get-WebBinding { @() }
    function script:Get-Item {
        param($Path)
        if ($Path -like 'Cert:*') { return [pscustomobject]@{HasPrivateKey=$true;NotBefore=(Get-Date).AddDays(-1);NotAfter=(Get-Date).AddDays(1)} }
        throw 'Unexpected synthetic item probe'
    }
    function script:Set-Acl { param($LiteralPath,$AclObject) $script:capturedAcl=$AclObject }
    function script:Get-Acl { param($LiteralPath) $script:capturedAcl }
} $facts
$plan = [pscustomobject]@{
    operationsManagerModule='C:\synthetic\OperationsManager.psd1';poolName='synthetic-pool';siteName='synthetic-site'
    applicationPath='/';httpsBaseUrl='https://synthetic.invalid';tlsThumbprint=('A'*40)
}
$count=0
function Check-Code([string]$Code) {
    $result=Get-ScomSetupPrerequisites $plan
    if ($Code -notin $result.codes -or $result.ready) { throw "Missing prerequisite failure: $Code" }
    $script:count++
}
try {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        if (-not (Get-ScomSetupPrerequisites $plan).ready) { throw 'Synthetic complete Desktop prerequisites rejected' }
        $count++
    } else { Check-Code 'WINDOWS_POWERSHELL_51_REQUIRED' }
    $facts.osBuild=12345; Check-Code 'REVIEW_SUPPORTED_WINDOWS_SERVER_REQUIRED'; $facts.osBuild=20348
    $facts.productType=1; Check-Code 'REVIEW_SUPPORTED_WINDOWS_SERVER_REQUIRED'; $facts.productType=3
    $facts.dotnet=$false; Check-Code 'DOTNET8_RUNTIME_REQUIRED'; $facts.dotnet=$true
    $facts.runtimeLines=@('Microsoft.NETCore.App 8.0.0 [synthetic]'); Check-Code 'Microsoft.AspNetCore.App_8_REQUIRED'
    $facts.runtimeLines=@('Microsoft.NETCore.App 8.0.0 [synthetic]','Microsoft.AspNetCore.App 8.0.0 [synthetic]')
    $facts.iis=$false; Check-Code 'IIS_MANAGEMENT_TOOLS_REQUIRED'; $facts.iis=$true
    $facts.iisServer=$false; Check-Code 'IIS_WEB_SERVER_REQUIRED'; $facts.iisServer=$true
    $facts.hostingBundle=$false; Check-Code 'DOTNET8_HOSTING_BUNDLE_REQUIRED'; $facts.hostingBundle=$true
    $facts.ancm=$false; Check-Code 'DOTNET8_HOSTING_BUNDLE_REQUIRED'; $facts.ancm=$true
    $facts.sdk=$false; Check-Code 'APPROVED_SCOM_CONSOLE_SDK_REQUIRED'; $facts.sdk=$true
    $facts.sdkMetadata=$false; Check-Code 'SCOM_SDK_ASSEMBLY_METADATA_REQUIRED'; $facts.sdkMetadata=$true
    $facts.osProbeFails=$true;Check-Code 'OS_PROBE_FAILED';$facts.osProbeFails=$false
    $facts.registryProbeFails=$true;Check-Code 'HOSTING_BUNDLE_PROBE_FAILED'
    $result=Get-ScomSetupPrerequisites $plan
    if (@($result.checks | Where-Object { $_.name -eq 'Local prerequisite probe completion' -and -not $_.passed }).Count -ne 1 -or
        ($result | ConvertTo-Json -Depth 10) -match 'private-marker') { throw 'Unreadable probe must be explicit, blocked and sanitized' }
    $count++;$facts.registryProbeFails=$false
    $facts.runtimeExit=1;Check-Code 'DOTNET8_RUNTIME_PROBE_FAILED';$facts.runtimeExit=0
    $facts.dotnet=$false;$facts.iis=$false;$facts.iisServer=$false;$facts.hostingBundle=$false;$facts.ancm=$false;$facts.sdk=$false;$facts.sdkMetadata=$false
    $missing=Get-ScomSetupPrerequisites $plan
    if ($missing.ready -or $missing.checks.Count -ne 10 -or
        @($missing.checks | Where-Object { $_.passed -and $_.name -match '\.NET|IIS|SCOM|OperationsManager' }).Count -ne 0) {
        throw 'Fresh server missing components must return individual blocked checks, not transport failure or false green'
    }
    $count++
    $facts.dotnet=$true;$facts.iis=$true;$facts.iisServer=$true;$facts.hostingBundle=$true;$facts.ancm=$true;$facts.sdk=$true;$facts.sdkMetadata=$true
    $checks=(Get-ScomSetupPrerequisites $plan).checks
    if (@($checks | Where-Object { -not $_.remedy }).Count -ne 0 -or
        @($checks | Where-Object { $_.name -match 'Local.*SCOM SDK' }).Count -ne 1) { throw 'Every actual prerequisite requires an actionable remedy and SDK metadata label' }
    $count++
    $facts.restart=$true; Check-Code 'RESTART_REQUIRED'; $facts.restart=$false
    foreach ($conflict in @('poolExists','siteExists')) {
        $facts.$conflict=$true
        try { & $module { param($p) Assert-ScomSetupIisTarget $p } $plan; throw 'Conflict unexpectedly accepted' }
        catch { if ($_.Exception.Message -notmatch 'SETUP_EXISTING_(POOL|SITE)_CONFLICT') { throw }; $count++ }
        $facts.$conflict=$false
    }
    & $module { param($p) Assert-ScomSetupIisTarget $p } $plan
    $count++
    $syntheticPath=Join-Path (Split-Path $PSScriptRoot -Parent) '.validation-data\synthetic-log-acl'
    Set-ScomSetupDirectoryAcl $syntheticPath @{'S-1-5-21-1-2-3-1001'='Modify'}
    & $module {
        $rules=@($script:capturedAcl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
        if ($rules.Count -ne 3 -or -not $script:capturedAcl.AreAccessRulesProtected -or
            @($rules | Where-Object { $_.IdentityReference.Value -eq 'S-1-5-21-1-2-3-1001' -and $_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Modify }).Count -ne 1) {
            throw 'Synthetic restrictive execution identity ACL mismatch'
        }
        function script:Get-Acl {
            param($LiteralPath)
            $changed=[Security.AccessControl.DirectorySecurity]::new()
            $changed.SetSecurityDescriptorBinaryForm($script:capturedAcl.GetSecurityDescriptorBinaryForm())
            $changed.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new('S-1-1-0'),'Modify','ContainerInherit,ObjectInherit','None','Allow'))
            $changed
        }
    }
    $count++
    try { Set-ScomSetupDirectoryAcl $syntheticPath @{}; throw 'ACL tampering unexpectedly accepted' }
    catch { if ($_.Exception.Message -notmatch 'SETUP_ACL_VERIFICATION_FAILED') { throw }; $count++ }
    Write-Output "PASS: $count synthetic Windows prerequisite/IIS-conflict checks; every external read probe replaced, no host mutation."
}
finally { Remove-Module ScomSetup.Windows }
