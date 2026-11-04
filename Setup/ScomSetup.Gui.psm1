Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomPrerequisiteInstall.psm1')

function Get-ScomGuiValue {
    param($Object,[string]$Name,$Default=$null)
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    $Default
}

function New-ScomGuiAdapters {
    @{
        Prerequisites = { param($p) Get-ScomSetupPrerequisites $p }
        IsAdmin = {
            $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
            $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        }
        Caller = { [Security.Principal.WindowsIdentity]::GetCurrent().Name }
        ValidateNew = { param($p) Assert-ScomSetupNewTargets $p }
        Private = { param($path) Assert-ScomSetupPrivateDirectory $path }
        CreateJournal = {
            param($path)
            $null = New-Item -ItemType Directory -Path $path
            Set-ScomSetupDirectoryAcl $path @{}
            Assert-ScomSetupPrivateDirectory $path
        }
        Discover = { param($p,$approve) Get-ScomSetupGroupCatalog $p -Approve $approve }
        Windows = { New-ScomSetupWindowsAdapters }
        PrerequisiteWindows = { param($path) New-ScomPrerequisiteWindowsAdapters $path }
        Worker = {
            param($p,$step,$approve)
            & (Join-Path $PSScriptRoot 'Start-ScomWorkerSetup.ps1') `
                -PlanPath (Join-Path $p.privatePath 'setup-plan.json') -Step $step -Approve $approve
        }
    }
}

function Invoke-ScomSetupGuiRequest {
    param([Parameter(Mandatory)]$Request,[Parameter(Mandatory)][string]$PackageRoot,
        [hashtable]$Adapters = (New-ScomGuiAdapters))
    $answer = [ordered]@{
        ok=$false; code='SETUP_GUI_FAILED'; message='No action completed. Review the displayed code and deployment guide.'
        checks=@(); groups=@(); stage=$null; pendingAction=$null; approvalRequired=$false; summary=$null; plan=$null
    }
    $approvalState = @{ supplied=[string](Get-ScomGuiValue $Request 'approval' ''); used=$false; required=$null }
    $approve = {
        param([string]$summary)
        if (-not $approvalState.used -and $approvalState.supplied -ceq $summary) {
            $approvalState.used=$true
            return $true
        }
        $approvalState.required=$summary
        throw 'SETUP_GUI_APPROVAL_REQUIRED'
    }
    $lease=$null
    $hashFile=Get-Command Get-ScomSetupHash -ErrorAction Stop
    try {
        $action=[string](Get-ScomGuiValue $Request 'action' '')
        if ($action -cnotin @('Preflight','Discover','Prepare','Configure','Activate','Status','Recover',
            'InstallPrerequisites','Connect','PublishWorker','Publish','Accept')) { throw 'SETUP_GUI_ACTION_INVALID' }
        $plan=Get-ScomGuiValue $Request 'plan'
        $stateDirectory=[string](Get-ScomGuiValue $Request 'stateDirectory' '')
        $planPath=[string](Get-ScomGuiValue $Request 'planPath' '')
        $workerLane=$action -cin @('Connect','PublishWorker') -or ($action -ceq 'Status' -and [bool]$planPath)
        if ($workerLane -and $planPath) {
            $plan=Read-ScomSetupJson $planPath
            if ([IO.Path]::GetFullPath($planPath) -ine (Join-Path $plan.privatePath 'setup-plan.json')) {
                throw 'SETUP_GUI_INSTALLED_WORKER_PLAN_REQUIRED'
            }
        }
        if ($null -eq $plan) { throw 'SETUP_GUI_PLAN_REQUIRED' }
        if ([IO.Path]::GetFullPath($plan.packagePath).TrimEnd('\') -ine [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')) {
            throw 'SETUP_GUI_PACKAGE_LOCATION_MISMATCH'
        }
        if ($action -eq 'Preflight') {
            Assert-ScomSetupPath $plan.operationsManagerModule
            $facts=& $Adapters.Prerequisites $plan
            $answer.checks=$facts.checks
            $answer.ok=[bool]$facts.ready
            $answer.code=if ($facts.ready) {'SETUP_GUI_PREFLIGHT_PASSED'} else {'SETUP_PREREQUISITES_BLOCKED'}
            $answer.message=if ($facts.ready) {
                'Prerequisites passed. No installation, source read or task registration occurred. Continue to scope and installation review.'
            } else {
                'Blocked. Install missing approved IIS/.NET prerequisites using the verified local installer page; SCOM console/SDK, signing, supported OS and reboot/maintenance require customer action. Rerun checks afterward.'
            }
            return [pscustomobject]$answer
        }
        if ($workerLane) {
            Assert-ScomSetupPlan $plan
            if ((& $Adapters.Caller) -ine $plan.workerAccount) { throw 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION' }
            if ($action -ceq 'Status') {
                if (-not $planPath) { throw 'SETUP_GUI_INSTALLED_WORKER_PLAN_REQUIRED' }
                $answer.ok=$true; $answer.code='SETUP_GUI_WORKER_PLAN_READY'; $answer.plan=$plan
                $answer.message='Installed worker plan loaded under the exact selected account. Host journal was not read; host stage, source evidence and acceptance are not inferred.'
                return [pscustomobject]$answer
            }
            $step=if ($action -eq 'Connect') {'Connect'} else {'Publish'}
            & $Adapters.Worker $plan $step $approve | Out-Null
            $answer.ok=$true; $answer.code='SETUP_GUI_WORKER_ACTION_COMPLETE'
            $answer.message='Worker action completed under the exact existing worker identity. Connection does not certify source contracts; publication does not certify reader acceptance.'
            return [pscustomobject]$answer
        }
        if ($action -eq 'Discover') {
            $facts=& $Adapters.Prerequisites $plan
            $answer.checks=$facts.checks
            if (-not $facts.ready) { throw 'SETUP_PREREQUISITES_BLOCKED' }
            $answer.groups=@(& $Adapters.Discover $plan $approve | ForEach-Object {
                $name=([string]$_.DisplayName -replace '[\x00-\x1f\x7f-\x9f]', ' ')
                if ($name.Length -gt 120) { $name=$name.Substring(0,120) }
                [pscustomobject]@{id=[string]$_.Id; displayName=$name}
            })
            if ($answer.groups.Count -gt 200) { throw 'SETUP_DISCOVERY_BUDGET_EXCEEDED' }
            $answer.ok=$true; $answer.code='SETUP_GUI_GROUPS_READY'
            $answer.message='Select an existing group, or separately confirm broader supported Windows inventory. No inventory or members were read.'
            return [pscustomobject]$answer
        }
        if (-not (& $Adapters.IsAdmin)) { throw 'SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE' }
        Assert-ScomSetupPath $stateDirectory
        foreach ($path in @($plan.packagePath,$plan.installPath,$plan.privatePath,$plan.receiverStatePath,
            $plan.workerStatePath,$plan.mcpLogPath,$plan.collectorLogPath)) {
            Assert-ScomSetupPath $path
            $a=[IO.Path]::GetFullPath($path).TrimEnd('\')+'\'
            $b=[IO.Path]::GetFullPath($stateDirectory).TrimEnd('\')+'\'
            if ($a.StartsWith($b,[StringComparison]::OrdinalIgnoreCase) -or $b.StartsWith($a,[StringComparison]::OrdinalIgnoreCase)) {
                throw 'SETUP_JOURNAL_PATH_COLLISION'
            }
        }
        $statePath=Join-Path $stateDirectory 'setup-state.json'
        $savedPlan=Join-Path $stateDirectory 'setup-plan.json'
        $hasState=Test-Path -LiteralPath $statePath
        if ($hasState) {
            & $Adapters.Private $stateDirectory
            $state=Read-ScomSetupJson $statePath
            if ($state.schemaVersion -ne 1 -or $state.planHash -cne (Get-ScomSetupHash $savedPlan)) {
                throw 'SETUP_PLAN_CHANGED_REVIEW_REQUIRED'
            }
            $plan=Read-ScomSetupJson $savedPlan
            Assert-ScomSetupPlan $plan
            if ([IO.Path]::GetFullPath($plan.packagePath).TrimEnd('\') -ine [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')) {
                throw 'SETUP_GUI_PACKAGE_LOCATION_MISMATCH'
            }
            $answer.stage=$state.stage; $answer.pendingAction=$state.pendingAction; $answer.plan=$plan
        }
        if ($action -eq 'Status') {
            if (-not $hasState) { throw 'SETUP_GUI_NO_SAVED_DEPLOYMENT' }
            $answer.ok=$true; $answer.code='SETUP_GUI_STATUS'
            $answer.message='Resumed protected deployment state. Pending actions require operator recovery; source/Entra review is not inferred.'
            return [pscustomobject]$answer
        }
        if ($action -eq 'InstallPrerequisites') {
            if (-not (Test-Path -LiteralPath $stateDirectory)) {
                if (-not (Test-Path -LiteralPath (Split-Path $stateDirectory -Parent) -PathType Container)) {
                    throw 'SETUP_EXISTING_PARENT_DIRECTORY_REQUIRED'
                }
                & $approve "Create ONLY the protected prerequisite/deployment journal $stateDirectory for SYSTEM and Administrators. No prerequisite installation yet." | Out-Null
                & $Adapters.CreateJournal $stateDirectory | Out-Null
                $answer.ok=$true; $answer.code='SETUP_GUI_JOURNAL_READY'
                $answer.message='Private journal created. Select Install prerequisites again to review each verified installation action separately.'
                return [pscustomobject]$answer
            }
            & $Adapters.Private $stateDirectory
            # The prerequisite helper asks for approval before any write. Its
            # approval callback acquires the shared lease only after approval.
            $prerequisiteLease=@{handle=$null}
            $prerequisiteJournal=Join-Path $stateDirectory 'prerequisite-state.json'
            $prerequisiteHash=if (Test-Path -LiteralPath $prerequisiteJournal) { Get-ScomSetupHash $prerequisiteJournal } else { $null }
            $prerequisiteApprove={
                param($summary)
                $accepted=& $approve $summary
                if ($accepted -and $null -eq $prerequisiteLease.handle) {
                    $prerequisiteLease.handle=[IO.File]::Open((Join-Path $stateDirectory 'setup.lock'),'OpenOrCreate','ReadWrite','None')
                    $currentHash=if (Test-Path -LiteralPath $prerequisiteJournal) { & $hashFile $prerequisiteJournal } else { $null }
                    if ($currentHash -cne $prerequisiteHash) { throw 'SETUP_GUI_CONCURRENT_STATE_CHANGED' }
                }
                $accepted
            }
            try {
            Invoke-ScomPrerequisiteInstall -JournalPath (Join-Path $stateDirectory 'prerequisite-state.json') `
                -Adapters (& $Adapters.PrerequisiteWindows $stateDirectory) `
                -InstallIis:([bool](Get-ScomGuiValue $Request 'installIis' $false)) `
                -InstallerPath ([string](Get-ScomGuiValue $Request 'hostingBundlePath' '')) `
                -ExpectedVersion ([string](Get-ScomGuiValue $Request 'hostingBundleVersion' '')) `
                -ExpectedSha256 ([string](Get-ScomGuiValue $Request 'hostingBundleSha256' '')) `
                -AcknowledgeRestart:([bool](Get-ScomGuiValue $Request 'acknowledgeRestart' $false)) -Approve $prerequisiteApprove | Out-Null
            }
            finally { if ($null -ne $prerequisiteLease.handle) { $prerequisiteLease.handle.Dispose() } }
            $answer.ok=$true; $answer.code='SETUP_GUI_PREREQUISITES_COMPLETE'
            $answer.message='Requested prerequisite actions completed. Reopen after approved customer IIS/OS maintenance and rerun Preflight; no automatic restart or app install.'
            return [pscustomobject]$answer
        }
        if ($action -ne 'Recover') {
            $facts=& $Adapters.Prerequisites $plan
            $answer.checks=$facts.checks
            if (-not $facts.ready) { throw 'SETUP_PREREQUISITES_BLOCKED' }
        }
        if (-not $hasState) {
            if ($action -ne 'Prepare') { throw 'SETUP_GUI_PREPARE_REQUIRED' }
            Assert-ScomSetupPlan $plan
            & $Adapters.ValidateNew $plan | Out-Null
            if (Test-Path -LiteralPath $stateDirectory) { & $Adapters.Private $stateDirectory }
            elseif (-not (Test-Path -LiteralPath (Split-Path $stateDirectory -Parent) -PathType Container)) {
                throw 'SETUP_EXISTING_PARENT_DIRECTORY_REQUIRED'
            }
            $summary="Create the protected deployment journal $stateDirectory and save this exact plan. Stage ONLY NEW install $($plan.installPath), private configuration $($plan.privatePath), receiver state $($plan.receiverStatePath), worker state $($plan.workerStatePath), MCP logs $($plan.mcpLogPath), collector logs $($plan.collectorLogPath), with restrictive ACLs. Scope: $(Get-ScomSetupScopeDescription $plan). No IIS activation, Entra changes, inventory collection or task registration."
            & $approve $summary | Out-Null
            if (-not (Test-Path -LiteralPath $stateDirectory)) { & $Adapters.CreateJournal $stateDirectory | Out-Null }
            $lease=[IO.File]::Open((Join-Path $stateDirectory 'setup.lock'),'OpenOrCreate','ReadWrite','None')
            if (Test-Path -LiteralPath $statePath) { throw 'SETUP_CONCURRENT_INITIALIZATION' }
            Save-ScomSetupJson $savedPlan $plan
            $state=[pscustomobject]@{schemaVersion=1; planHash=(Get-ScomSetupHash $savedPlan); stage='new'; pendingAction=$null; receiverHash=$null; workerHash=$null}
            Save-ScomSetupJson $statePath $state
            $state=Invoke-ScomSetupTransition $plan $state $statePath Prepare (& $Adapters.Windows) -Approve {$true}
        } else {
            $windows=& $Adapters.Windows
            if ($action -eq 'Recover') {
                if (-not $state.pendingAction -and $state.stage -notin @('activated','published','accepted')) { throw 'SETUP_NO_RUNNING_ACTION_TO_RECOVER' }
                $recoveryHash=Get-ScomSetupHash $statePath
                & $approve "Stop and disable startup ONLY for the exactly owned dedicated IIS pool $($plan.poolName). Preserve all data and the pending journal for manual recovery; no rollback is claimed." | Out-Null
                $lease=[IO.File]::Open((Join-Path $stateDirectory 'setup.lock'),'OpenOrCreate','ReadWrite','None')
                if ($recoveryHash -cne (Get-ScomSetupHash $statePath) -or $state.planHash -cne (Get-ScomSetupHash $savedPlan)) {
                    throw 'SETUP_GUI_CONCURRENT_STATE_CHANGED'
                }
                & $windows.Stop $plan | Out-Null
            } else {
                $inputSettings=[pscustomobject]@{
                    identityPath=[string](Get-ScomGuiValue $Request 'identityPath' '')
                    receiverPath=[string](Get-ScomGuiValue $Request 'receiverSettingsPath' '')
                    workerPath=[string](Get-ScomGuiValue $Request 'workerSettingsPath' '')
                }
                if ($action -eq 'Configure' -and (-not $inputSettings.identityPath -or -not $inputSettings.receiverPath -or -not $inputSettings.workerPath)) {
                    throw 'SETUP_PAUSED_IDENTITY_AND_REVIEWED_CONFIG_REQUIRED'
                }
                $stateHash=Get-ScomSetupHash $statePath
                $transitionLease=@{handle=$null}
                $transitionApprove={
                    param($summary)
                    $accepted=& $approve $summary
                    if ($accepted) {
                        $transitionLease.handle=[IO.File]::Open((Join-Path $stateDirectory 'setup.lock'),'OpenOrCreate','ReadWrite','None')
                        if ($stateHash -cne (& $hashFile $statePath) -or
                            $state.planHash -cne (& $hashFile $savedPlan)) { throw 'SETUP_GUI_CONCURRENT_STATE_CHANGED' }
                    }
                    $accepted
                }.GetNewClosure()
                try {
                    $state=Invoke-ScomSetupTransition $plan $state $statePath $action $windows -Approve $transitionApprove -InputSettings $inputSettings
                }
                finally { if ($null -ne $transitionLease.handle) { $transitionLease.handle.Dispose() } }
            }
        }
        $answer.ok=$true; $answer.code='SETUP_GUI_ACTION_COMPLETE'
        $answer.stage=$state.stage; $answer.pendingAction=$state.pendingAction; $answer.plan=$plan
        $answer.message='Action completed. Follow the next stage; Entra permissions, source evidence, worker identity and reader acceptance remain separate gates. Metrics/live checks/recurrence remain disabled by setup.'
    }
    catch {
        if ($approvalState.required) {
            $answer.code='SETUP_GUI_APPROVAL_REQUIRED'; $answer.approvalRequired=$true
            $answer.summary=$approvalState.required
            $answer.message='Review this exact action. Cancel performs no unapproved effect. Earlier independently approved prerequisite actions may already have completed.'
        } else {
            $code=[regex]::Match($_.Exception.Message,'^SETUP_[A-Z0-9_]+').Value
            if ($code) { $answer.code=$code }
            $answer.message='Blocked or failed. No success is claimed. Preserve any pending journal and private logs; review the code and deployment guide. Signing/UAC/identity/source prerequisites must be resolved through the customer process.'
        }
    }
    finally { if ($null -ne $lease) { $lease.Dispose() } }
    [pscustomobject]$answer
}

Export-ModuleMember -Function Invoke-ScomSetupGuiRequest, New-ScomGuiAdapters
