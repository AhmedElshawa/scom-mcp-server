[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [ValidateSet('InstallPrerequisites','Prepare','Configure','Activate','Publish','Accept','Status','Recover')][string]$Step = 'Status',
    [string]$PlanPath,
    [string]$IdentityPath,
    [string]$ReceiverSettingsPath,
    [string]$WorkerSettingsPath,
    [switch]$InstallIis,
    [string]$HostingBundlePath,
    [string]$HostingBundleVersion,
    [string]$HostingBundleSha256,
    [switch]$AcknowledgeRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE' }
Assert-ScomSetupPrivateDirectory $StateDirectory
$statePath = Join-Path $StateDirectory 'setup-state.json'
$savedPlan = Join-Path $StateDirectory 'setup-plan.json'
# An exclusive lease prevents competing resumptions, including across consoles.
$lease = [IO.File]::Open((Join-Path $StateDirectory 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    if ($Step -eq 'InstallPrerequisites') {
        & (Join-Path $PSScriptRoot 'Install-ScomPrerequisites.ps1') -StateDirectory $StateDirectory `
            -InstallIis:$InstallIis -HostingBundlePath $HostingBundlePath -HostingBundleVersion $HostingBundleVersion `
            -HostingBundleSha256 $HostingBundleSha256 -AcknowledgeRestart:$AcknowledgeRestart
        return
    }
    if (-not (Test-Path -LiteralPath $statePath)) {
        if ($Step -ne 'Prepare') { throw 'SETUP_FIRST_STEP_MUST_BE_PREPARE' }
        if ($PlanPath) { $plan = Read-ScomSetupJson $PlanPath }
        else {
            $plan = [pscustomobject][ordered]@{
                schemaVersion=1
                packagePath=(Read-Host 'Reviewed complete package directory')
                installPath=(Read-Host 'NEW installation directory (no existing deployment)')
                privatePath=(Read-Host 'NEW private worker configuration directory')
                receiverStatePath=(Read-Host 'NEW receiver database directory')
                workerStatePath=(Read-Host 'NEW worker checkpoint directory')
                siteName=(Read-Host 'New site name, or existing site for a NEW subapplication')
                applicationPath=(Read-Host 'Application path (/ for a new site, or /monitoring for a new subapplication)')
                poolName=(Read-Host 'NEW dedicated application pool name')
                httpsBaseUrl=(Read-Host 'Approved HTTPS application base URL including prefix')
                tlsThumbprint=(Read-Host 'Existing LocalMachine My TLS certificate thumbprint')
                workerAccount=(Read-Host 'Existing approved read-only SCOM worker DOMAIN\account')
                managementServer=(Read-Host 'SCOM management server FQDN')
                managementGroupId=(Read-Host 'Approved management-group GUID')
                groupId=(Read-Host 'Existing approved group GUID (never created or expanded)')
                operationsManagerModule=(Read-Host 'Approved installed OperationsManager.psd1 absolute path')
            }
        }
        Assert-ScomSetupPlan $plan
        foreach ($path in @($plan.packagePath,$plan.installPath,$plan.privatePath,$plan.receiverStatePath,$plan.workerStatePath)) {
            $a = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
            $b = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\') + '\'
            if ($a.StartsWith($b,[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a,[StringComparison]::OrdinalIgnoreCase)) {
                throw 'SETUP_JOURNAL_PATH_COLLISION'
            }
        }
        if (-not (Confirm-ScomSetupEffect 'Save this private deployment plan and resumable journal; no host installation yet.')) { throw 'SETUP_CANCELLED_NO_ACTION' }
        Save-ScomSetupJson $savedPlan $plan
        $state = [pscustomobject]@{
            schemaVersion=1; planHash=(Get-ScomSetupHash $savedPlan); stage='new'; pendingAction=$null
            receiverHash=$null; workerHash=$null
        }
        Save-ScomSetupJson $statePath $state
    }
    $state = Read-ScomSetupJson $statePath
    if ($state.schemaVersion -ne 1 -or $state.planHash -ne (Get-ScomSetupHash $savedPlan)) { throw 'SETUP_PLAN_CHANGED_REVIEW_REQUIRED' }
    $plan = Read-ScomSetupJson $savedPlan
    Assert-ScomSetupPlan $plan
    $adapters = New-ScomSetupWindowsAdapters
    if ($Step -eq 'Status') {
        Write-Output "Stage=$($state.stage); PendingAction=$($state.pendingAction). Journal is not certification."
        Get-ScomSetupPrerequisites $plan
        return
    }
    if ($Step -eq 'Recover') {
        if (-not $state.pendingAction -and $state.stage -notin @('activated','published','accepted')) { throw 'SETUP_NO_RUNNING_ACTION_TO_RECOVER' }
        if (Confirm-ScomSetupEffect "Disable normal IIS auto-start and stop ONLY the owned dedicated pool $($plan.poolName). Leave all files, site, certificates and databases intact; pending journal remains for manual review.") {
            & $adapters.Stop $plan
            Write-Output 'SETUP_POOL_STOPPED. No configuration/database rollback claimed. Resolve journal and partial resources under the documented recovery procedure.'
        }
        return
    }
    if ($Step -eq 'Configure' -and (-not $IdentityPath -or -not $ReceiverSettingsPath -or -not $WorkerSettingsPath)) {
        Write-Output 'SETUP_PAUSED_IDENTITY_AND_REVIEWED_CONFIG_REQUIRED. Run separate Entra provisioning with its authorized administrator, then resume Configure with nonsecret identity and both reviewed JSON paths.'
        return
    }
    $inputSettings = [pscustomobject]@{identityPath=$IdentityPath; receiverPath=$ReceiverSettingsPath; workerPath=$WorkerSettingsPath}
    $state = Invoke-ScomSetupTransition $plan $state $statePath $Step $adapters -InputSettings $inputSettings
    if ($Step -eq 'Prepare') {
        Write-Output 'Prepared, not active. As the intended worker, use Start-ScomWorkerSetup.ps1 -Step Connect to establish the explicit bounded connection and existing-group check. Then separately obtain identity and review source mapping/contracts before Configure.'
    }
    if ($Step -eq 'Activate') {
        Write-Output 'HTTPS reachable only; inventory and reader acceptance NOT proven. As the intended worker, use Start-ScomWorkerSetup.ps1 -Step Publish for the separately approved one-shot run, then resume this host lane at Publish.'
    }
    if ($Step -eq 'Publish') { Write-Output 'Publication observed; complete intended-user Studio/tool and unauthorized-user acceptance before Accept.' }
    Write-Output "SETUP_STAGE=$($state.stage). No recurrence, metrics or targeted-live enabled."
}
finally { $lease.Dispose() }
