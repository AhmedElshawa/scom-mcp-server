[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [ValidateSet('Preflight','InstallPrerequisites','Prepare','Configure','Activate','Publish','Accept','Status','Recover')][string]$Step = 'Preflight',
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
$lease = $null
try {
    if ($Step -eq 'InstallPrerequisites') {
        & (Join-Path $PSScriptRoot 'Install-ScomPrerequisites.ps1') -StateDirectory $StateDirectory `
            -InstallIis:$InstallIis -HostingBundlePath $HostingBundlePath -HostingBundleVersion $HostingBundleVersion `
            -HostingBundleSha256 $HostingBundleSha256 -AcknowledgeRestart:$AcknowledgeRestart
        return
    }
    if (-not (Test-Path -LiteralPath $statePath)) {
        if ($Step -notin @('Preflight','Prepare')) { throw 'SETUP_FIRST_STEP_MUST_BE_PREFLIGHT_OR_PREPARE' }
        if ($PlanPath) { $plan = Read-ScomSetupJson $PlanPath }
        else {
            $plan = [pscustomobject][ordered]@{
                schemaVersion=1
                packagePath=(Read-Host 'Reviewed complete package directory')
                installPath=(Read-Host 'NEW installation directory (no existing deployment)')
                privatePath=(Read-Host 'NEW private worker configuration directory')
                receiverStatePath=(Read-Host 'NEW receiver database directory')
                workerStatePath=(Read-Host 'NEW worker checkpoint directory')
                mcpLogPath=(Read-Host 'NEW MCP structured log directory on local NTFS')
                collectorLogPath=(Read-Host 'NEW collector/worker structured log directory on local NTFS (separate from MCP)')
                siteName=(Read-Host 'New site name, or existing site for a NEW subapplication')
                applicationPath=(Read-Host 'Application path (/ for a new site, or /monitoring for a new subapplication)')
                poolName=(Read-Host 'NEW dedicated application pool name')
                httpsBaseUrl=(Read-Host 'Approved HTTPS application base URL including prefix')
                tlsThumbprint=(Read-Host 'Existing LocalMachine My TLS certificate thumbprint')
                workerAccount=(Read-Host 'Existing approved read-only SCOM worker DOMAIN\account')
                managementServer=(Read-Host 'SCOM management server FQDN')
                managementGroupId=(Read-Host 'Approved management-group GUID')
                scopeKind='group'
                groupId=$null
                wholeManagementGroupApproved=$false
                operationsManagerModule=(Read-Host 'Approved installed OperationsManager.psd1 absolute path')
            }
        }
        Assert-ScomSetupPath $plan.operationsManagerModule
        if ($Step -notin @('Recover','Status')) { Show-ScomSetupPrerequisites (Get-ScomSetupPrerequisites $plan) }
        if ($Step -eq 'Preflight') { return }
        if (-not $PlanPath) {
            $catalog = @(Get-ScomSetupGroupCatalog $plan)
            for ($i = 0; $i -lt $catalog.Count; $i++) {
                # Strip console-control characters; source display names are untrusted.
                $display = ([string]$catalog[$i].DisplayName -replace '[\x00-\x1f\x7f-\x9f]', ' ')
                if ($display.Length -gt 120) { $display = $display.Substring(0,120) }
                Write-Host "$($i + 1): $display"
            }
            Set-ScomSetupScopeSelection $plan $catalog (Read-Host 'Choose an existing group number, or type ALL for all supported Windows inventory (not every SCOM class)')
        } elseif ($plan.scopeKind -ceq 'managementGroup') {
            Set-ScomSetupScopeSelection $plan @() 'ALL'
        }
        Assert-ScomSetupPlan $plan
        Assert-ScomSetupNewTargets $plan
        foreach ($path in @($plan.packagePath,$plan.installPath,$plan.privatePath,$plan.receiverStatePath,$plan.workerStatePath,$plan.mcpLogPath,$plan.collectorLogPath)) {
            $a = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
            $b = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\') + '\'
            if ($a.StartsWith($b,[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a,[StringComparison]::OrdinalIgnoreCase)) {
                throw 'SETUP_JOURNAL_PATH_COLLISION'
            }
        }
        if (-not (Confirm-ScomSetupEffect 'Save this private deployment plan and resumable journal; no host installation yet.')) { throw 'SETUP_CANCELLED_NO_ACTION' }
        # Acquire a mutation lease only after read-only preflight and approval.
        $lease = [IO.File]::Open((Join-Path $StateDirectory 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        if (Test-Path -LiteralPath $statePath) { throw 'SETUP_CONCURRENT_INITIALIZATION' }
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
    if ($Step -notin @('Recover','Status')) { Show-ScomSetupPrerequisites (Get-ScomSetupPrerequisites $plan) }
    if ($Step -eq 'Preflight') { return }
    if ($null -eq $lease -and $Step -ne 'Status') {
        $lease = [IO.File]::Open((Join-Path $StateDirectory 'setup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        $state = Read-ScomSetupJson $statePath
        if ($state.planHash -ne (Get-ScomSetupHash $savedPlan)) { throw 'SETUP_PLAN_CHANGED_REVIEW_REQUIRED' }
    }
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
finally { if ($null -ne $lease) { $lease.Dispose() } }
