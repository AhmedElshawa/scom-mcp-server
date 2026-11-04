[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlanPath,
    [Parameter(Mandatory)][ValidateSet('Connect','Publish')][string]$Step
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
$plan = Read-ScomSetupJson $PlanPath
Assert-ScomSetupPlan $plan
if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5) { throw 'SETUP_WINDOWS_POWERSHELL51_REQUIRED' }
if ([Security.Principal.WindowsIdentity]::GetCurrent().Name -ne $plan.workerAccount) { throw 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION' }
if ($Step -eq 'Connect') {
    if (-not (Confirm-ScomSetupEffect "Connect to $($plan.managementServer), check ONLY management-group identity and, for group scope, its existing group GUID. Selected scope: $(Get-ScomSetupScopeDescription $plan). Timeout 60 seconds; no members, alerts or inventory enumerated; no scope flags set.")) {
        throw 'SETUP_CANCELLED_NO_ACTION'
    }
    $job = Start-Job -ArgumentList $plan,(Join-Path $PSScriptRoot 'ScomSetup.psm1') -ScriptBlock {
        param($p,$modulePath)
        $ErrorActionPreference = 'Stop'
        Import-Module $modulePath
        Import-Module $p.operationsManagerModule
        New-SCOMManagementGroupConnection -ComputerName $p.managementServer -ErrorAction Stop | Out-Null
        $groups = @(Get-SCOMManagementGroup -ErrorAction Stop)
        $group = @()
        if ($p.scopeKind -ceq 'group') { $group = @(Get-SCOMGroup -Id ([guid]$p.groupId) -ErrorAction Stop) }
        Assert-ScomSetupConnectionIdentity $p $groups $group
        'SCOM_SETUP_CONNECTION_GROUP_VISIBLE_NOT_SCOPE_CERTIFIED'
    }
    try {
        if (-not (Wait-Job $job -Timeout 60)) { Stop-Job $job; throw 'SETUP_CONNECTION_TIMEOUT' }
        if ($job.State -ne 'Completed') { throw 'SETUP_CONNECTION_FAILED' }
        Receive-Job $job -ErrorAction Stop
    }
    finally { Remove-Job $job -Force }
    return
}
Invoke-ScomSetupWorkerPublication -Plan $plan -CallerName ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -Validate {
    param($p)
    $server = Join-Path $p.installPath 'server'
    & dotnet (Join-Path $server 'ScomMcp.dll') --validate-setup $server (Join-Path $p.privatePath 'worker.json')
    if ($LASTEXITCODE -ne 0) { throw 'SETUP_CONFIGURATION_OR_SOURCE_EVIDENCE_BLOCKED' }
} -RunOnce {
    param($p)
    $output = & dotnet (Join-Path $p.installPath 'server\ScomMcp.dll') --worker-once (Join-Path $p.privatePath 'worker.json')
    [pscustomobject]@{exitCode=$LASTEXITCODE;published=[bool]($output -match 'SCOM_WORKER_PUBLISHED')}
}
Write-Output 'SCOM_WORKER_PUBLISHED observed after checkpoint acknowledgement. Receipt is worker-reported operational evidence, not reader acceptance or certification.'
