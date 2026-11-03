Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1') -Force
$module = Get-Module ScomSetup.Windows
$facts = [pscustomobject]@{
    osBuild=20348; productType=3; dotnet=$true; runtimeLines=@('Microsoft.NETCore.App 8.0.0 [synthetic]','Microsoft.AspNetCore.App 8.0.0 [synthetic]')
    iis=$true; ancm=$true; hostingBundle=$true; sdk=$true; restart=$false; poolExists=$false; siteExists=$false
}
# Substitute read-only command probes INSIDE the adapter module. No IIS
# commands, registry writes, runtime execution or source connection can occur.
& $module {
    param($fixture)
    $script:facts=$fixture
    function script:Get-CimInstance { [pscustomobject]@{BuildNumber=$script:facts.osBuild;ProductType=$script:facts.productType} }
    function script:Get-Command {
        if ($script:facts.dotnet) { [pscustomobject]@{Source='synthetic-dotnet'} }
    }
    function script:synthetic-dotnet {
        $global:LASTEXITCODE=0
        $script:facts.runtimeLines
    }
    function script:Get-Module { if ($script:facts.iis) { [pscustomobject]@{Name='synthetic-IIS'} } }
    function script:Import-Module {}
    function script:Get-ItemProperty {
        param($Path)
        if ($Path -match 'Uninstall') {
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
    $facts.hostingBundle=$false; Check-Code 'DOTNET8_HOSTING_BUNDLE_REQUIRED'; $facts.hostingBundle=$true
    $facts.ancm=$false; Check-Code 'DOTNET8_HOSTING_BUNDLE_REQUIRED'; $facts.ancm=$true
    $facts.sdk=$false; Check-Code 'APPROVED_SCOM_CONSOLE_SDK_REQUIRED'; $facts.sdk=$true
    $facts.restart=$true; Check-Code 'RESTART_REQUIRED'; $facts.restart=$false
    foreach ($conflict in @('poolExists','siteExists')) {
        $facts.$conflict=$true
        try { & $module { param($p) Assert-ScomSetupIisTarget $p } $plan; throw 'Conflict unexpectedly accepted' }
        catch { if ($_.Exception.Message -notmatch 'SETUP_EXISTING_(POOL|SITE)_CONFLICT') { throw }; $count++ }
        $facts.$conflict=$false
    }
    & $module { param($p) Assert-ScomSetupIisTarget $p } $plan
    $count++
    Write-Output "PASS: $count synthetic Windows prerequisite/IIS-conflict checks; every external read probe replaced, no host mutation."
}
finally { Remove-Module ScomSetup.Windows }
