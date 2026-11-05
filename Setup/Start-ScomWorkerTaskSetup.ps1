[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StateDirectory,
    [ValidateSet('Register','Start','Status','Stop')][string]$Step='Status'
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$lease=$null
$failed=$false
try {
    Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
    Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE'
    }
    Assert-ScomSetupPrivateDirectory $StateDirectory
    $lease=[IO.File]::Open((Join-Path $StateDirectory 'setup.lock'),'Open','ReadWrite','None')
    $arguments=& (Join-Path $PSScriptRoot 'Get-ScomWorkerDeployment.ps1') -StateDirectory $StateDirectory -ExistingTask:($Step -ne 'Register')
    $plan=Read-ScomSetupJson (Join-Path $StateDirectory 'setup-plan.json')
    $operations=Join-Path (Split-Path $PSScriptRoot -Parent) 'Operations\DurableIndexedRefresh'
    if ($Step -eq 'Register') {
        Write-Host 'OPT-IN RECURRENCE: registration installs an AtStartup task. It may run after reboot; it is not started now.'
        Write-Host 'The account needs approved SCOM read access, publisher-key access and batch logon. Windows stores the task credential.'
        $credential=Get-Credential -UserName $plan.workerAccount -Message 'Existing selected worker account for this accepted deployment'
        if ($null -eq $credential -or $credential.UserName -ine $plan.workerAccount) {
            throw 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION'
        }
        & (Join-Path $operations 'Register-ScomWorkerTask.ps1') @arguments -Credential $credential -Confirm
    } else {
        . (Join-Path $operations 'DurableIndexedRefresh.Common.ps1')
        $task=Get-OwnedRefreshTask -TaskName 'SCOM-MCP Durable Indexed Refresh' -TaskPath '\'
        $action=Get-RefreshTaskActionInfo -Task $task -RequireFiles
        Assert-ScomSetupWorkerTaskMatch $arguments $task $action $plan.workerAccount
        $scriptName=@{Start='Start-ScomWorkerTask.ps1';Status='Get-ScomWorkerTaskStatus.ps1';Stop='Stop-ScomWorkerTask.ps1'}[$Step]
        if ($Step -eq 'Status') { & (Join-Path $operations $scriptName) }
        else { & (Join-Path $operations $scriptName) -Confirm }
    }
} catch {
    $failed=$true
    $code='SETUP_TASK_HANDOFF_FAILED'
    if ($_.Exception.Message -cin @('SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE',
        'SETUP_ACCEPTANCE_REQUIRED_BEFORE_RECURRENCE','SETUP_SAVED_PLAN_CHANGED',
        'SETUP_ACCEPTED_CONFIGURATION_CHANGED','SETUP_WORKER_PATH_OR_DISABLED_GUARD_CHANGED',
        'SETUP_EXISTING_RUNTIME_REQUIRES_TASK_STATUS_NOT_NEW_REGISTRATION',
        'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION','SETUP_TASK_DEPLOYMENT_MISMATCH',
        'SETUP_INSTALLED_PACKAGE_CHANGED','SETUP_INSTALLED_MANIFEST_INVALID')) {
        $code=$_.Exception.Message
    }
    Write-Error "$code`: No success is claimed. Preserve task/state and inspect through the administrator process; do not assume rollback." -ErrorAction Continue
} finally {
    $credential=$null
    if ($null -ne $lease) { $lease.Dispose() }
    $null=Read-Host 'Press Enter to close this task setup window'
}
if ($failed) { exit 1 }
