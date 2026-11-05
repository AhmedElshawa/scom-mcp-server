Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomPrerequisiteInstall.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
$root=Join-Path (Split-Path $PSScriptRoot -Parent) ('.validation-data\prerequisite-' + [guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$script:count=0
function Check([bool]$value) { if (-not $value) { throw 'Prerequisite assertion failed' }; $script:count++ }
function Reject([scriptblock]$body,[string]$code) {
    try { & $body } catch { if ($_.Exception.Message -notlike "*$code*") { throw }; $script:count++; return }
    throw "Expected $code"
}
$facts=[pscustomobject]@{supportedHost=$true;bootUtc='2026-10-01T00:00:00Z';restartPending=$false;iisReady=$false;exactBundleInstalled=$false;runtimeReady=$false;ancmReady=$false}
$evidence=[pscustomobject]@{fileName='dotnet-hosting-8.0.22-win.exe';productVersion='8.0.22.123';sha256=('A'*64);signatureStatus='Valid';signerSubject='CN=Microsoft Corporation, O=Microsoft Corporation, C=US'}
$control=[pscustomobject]@{iis=0;bundle=0;fail=$null;reboot=$false;mode=$null;exitCode=0}
$adapters=@{
    Probe={param($version) $facts}.GetNewClosure()
    Inspect={param($path) $evidence}.GetNewClosure()
    InstallIis={
        $control.iis++
        if ($control.fail -eq 'iis') { throw 'SYNTHETIC_IIS_FAILURE' }
        $facts.iisReady=$true
        [pscustomobject]@{success=$true;restartRequired=$control.reboot;outcome='Success'}
    }.GetNewClosure()
    HostingBundle={
        param($path,$version,$hash,$mode)
        $control.bundle++; $control.mode=$mode
        if ($control.fail -eq 'bundle') { throw 'SYNTHETIC_BUNDLE_FAILURE' }
        $facts.exactBundleInstalled=$true;$facts.runtimeReady=$true;$facts.ancmReady=$true
        [pscustomobject]@{success=($control.exitCode -in @(0,3010));restartRequired=($control.exitCode -eq 3010);outcome="Exit$($control.exitCode)";processId=12345}
    }.GetNewClosure()
}
$argsBase=@{Adapters=$adapters;InstallerPath=(Join-Path $root 'dotnet-hosting-8.0.22-win.exe');ExpectedVersion='8.0.22';ExpectedSha256=('A'*64);Approve={$true};InstallIis=$true}
try {
    Assert-ScomHostingInstallerEvidence $evidence '8.0.22' ('A'*64); Check $true
    foreach ($field in @('fileName','productVersion','sha256','signatureStatus','signerSubject')) {
        $before=$evidence.$field
        $evidence.$field='unapproved'
        Reject { Assert-ScomHostingInstallerEvidence $evidence '8.0.22' ('A'*64) } 'SETUP_HOSTING_INSTALLER_VENDOR'
        $evidence.$field=$before
    }
    Reject { Assert-ScomHostingInstallerEvidence $evidence '9.0.1' ('A'*64) } 'SETUP_HOSTING_INSTALLER_VENDOR'
    $journal=Join-Path $root 'initial.json'
    Reject { Invoke-ScomPrerequisiteInstall -JournalPath $journal -Adapters $adapters } 'REQUIRES_EXPLICIT_OPT_IN'
    $arguments=$argsBase.Clone();$arguments.JournalPath=$journal;$arguments.Approve={$false}
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'SETUP_CANCELLED_NO_ACTION'
    Check ($control.iis -eq 0 -and $control.bundle -eq 0 -and -not (Test-Path -LiteralPath $journal))
    $facts.supportedHost=$false;$arguments.Approve={$true}
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'UNSUPPORTED_HOST';$facts.supportedHost=$true
    $facts.restartPending=$true
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'EXISTING_RESTART_REQUIRED';$facts.restartPending=$false
    $evidence.signatureStatus='HashMismatch'
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'SETUP_HOSTING_INSTALLER_VENDOR';$evidence.signatureStatus='Valid'
    Check ($control.iis -eq 0 -and $control.bundle -eq 0)
    Invoke-ScomPrerequisiteInstall @arguments | Out-Null
    Check ($control.iis -eq 1 -and $control.bundle -eq 1 -and $control.mode -eq 'install')
    $record=Read-ScomSetupJson $journal
    Check ($record.completed.Count -eq 2 -and -not $record.pendingAction)
    Invoke-ScomPrerequisiteInstall @arguments | Out-Null
    Check ($control.iis -eq 1 -and $control.bundle -eq 2 -and $control.mode -eq 'repair')
    foreach ($failure in @('iis','bundle')) {
        $facts.iisReady=($failure -eq 'bundle');$control.fail=$failure
        $arguments.JournalPath=Join-Path $root "failure-$failure.json"
        Reject { Invoke-ScomPrerequisiteInstall @arguments } "SYNTHETIC_$($failure.ToUpper())_FAILURE"
        Check ([bool](Read-ScomSetupJson $arguments.JournalPath).pendingAction)
        $before=$control.iis+$control.bundle
        Reject { Invoke-ScomPrerequisiteInstall @arguments } 'INTERRUPTED_PREREQUISITE_EFFECT'
        Check ($control.iis+$control.bundle -eq $before)
    }
    $control.fail=$null;$facts.iisReady=$false;$control.reboot=$true
    $arguments.JournalPath=Join-Path $root 'iis-reboot.json'
    $before=$control.bundle
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'PREREQUISITE_RESTART_REQUIRED'
    Check ($control.bundle -eq $before -and -not (Read-ScomSetupJson $arguments.JournalPath).pendingAction)
    Check ([bool](Read-ScomSetupJson $arguments.JournalPath).restartBootUtc)
    $arguments.AcknowledgeRestart=$true
    Reject { Invoke-ScomPrerequisiteInstall @arguments } 'PREREQUISITE_RESTART_REQUIRED'
    $facts.bootUtc='2026-10-02T00:00:00Z';$control.reboot=$false
    Invoke-ScomPrerequisiteInstall @arguments | Out-Null
    Check (-not (Read-ScomSetupJson $arguments.JournalPath).restartBootUtc)
    foreach ($exitCode in @(3010,1603,1641,-1)) {
        $arguments.JournalPath=Join-Path $root "bundle-exit-$exitCode.json"
        $control.exitCode=$exitCode
        $code=if ($exitCode -eq 3010) {'PREREQUISITE_RESTART_REQUIRED'} else {'EFFECT_FAILED_OR_TIMED_OUT'}
        Reject { Invoke-ScomPrerequisiteInstall @arguments } $code
        $record=Read-ScomSetupJson $arguments.JournalPath
        Check ($record.processId -eq 12345)
        Check ($(if ($exitCode -eq 3010) { [bool]$record.restartBootUtc -and -not $record.pendingAction } else { [bool]$record.pendingAction }))
    }
    Write-Output "PASS: $script:count prerequisite installer assertions; vendor/version/checksum, opt-in, every fake effect, interrupted no-retry, exit/reboot and repair. No installers or OS changes."
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
