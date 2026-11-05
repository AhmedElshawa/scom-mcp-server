[CmdletBinding()]
param([Parameter(Mandatory)][string]$StateDirectory,[switch]$ExistingTask)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
Assert-ScomSetupPrivateDirectory $StateDirectory
$state=Read-ScomSetupJson (Join-Path $StateDirectory 'setup-state.json')
$planPath=Join-Path $StateDirectory 'setup-plan.json'
$plan=Read-ScomSetupJson $planPath
if ($state.planHash -cne (Get-ScomSetupHash $planPath)) { throw 'SETUP_SAVED_PLAN_CHANGED' }
$arguments=Get-ScomSetupWorkerTaskArguments $plan $state
$workerSid=([Security.Principal.NTAccount]::new($plan.workerAccount)).Translate([Security.Principal.SecurityIdentifier])
Assert-ScomSetupPrivateDirectory $plan.privatePath -ReadSid $workerSid
Assert-ScomSetupInstalledFiles $plan
$workerPath=$arguments.WorkerConfigPath
$receiverPath=Join-Path $plan.installPath 'server\appsettings.json'
if ($state.workerHash -cne (Get-ScomSetupHash $workerPath) -or
    $state.receiverHash -cne (Get-ScomSetupHash $receiverPath)) { throw 'SETUP_ACCEPTED_CONFIGURATION_CHANGED' }
$worker=Read-ScomSetupJson $workerPath
if ($worker.checkpointPath -ine (Join-Path $plan.workerStatePath 'worker.db') -or
    $worker.profile.scriptPath -ine (Join-Path $plan.installPath 'collector\Invoke-SCOMGenericGroupProfile.ps1') -or
    $worker.profile.scriptSha256 -cne (Get-ScomSetupHash $worker.profile.scriptPath) -or
    $worker.operationalLogging.directory -ine $plan.collectorLogPath -or
    $worker.liveChecks.enabled -ne $false) { throw 'SETUP_WORKER_PATH_OR_DISABLED_GUARD_CHANGED' }
Assert-ScomSetupPath $arguments.StateDirectory
if (-not $ExistingTask -and (Test-Path -LiteralPath $arguments.StateDirectory) -and
    @((Get-ChildItem -LiteralPath $arguments.StateDirectory -Force)).Count -ne 0) {
    throw 'SETUP_EXISTING_RUNTIME_REQUIRES_TASK_STATUS_NOT_NEW_REGISTRATION'
}
# Return a splattable path map only. Credentials and registration remain separate explicit actions.
$arguments
