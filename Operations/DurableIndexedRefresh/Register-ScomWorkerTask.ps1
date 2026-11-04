[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][string]$WorkerDllPath,
    [Parameter(Mandatory = $true)][string]$WorkerConfigPath,
    [Parameter(Mandatory = $true)][string]$StateDirectory,
    [Parameter(Mandatory = $true)][PSCredential]$Credential,
    [string]$TaskName = 'SCOM-MCP Durable Indexed Refresh',
    [string]$TaskPath = '\',
    [switch]$StartNow
)

. (Join-Path $PSScriptRoot 'DurableIndexedRefresh.Common.ps1')
Assert-RefreshTaskSelector -TaskName $TaskName -TaskPath $TaskPath
$identity = Resolve-RefreshTaskIdentity -Credential $Credential
$dll = Resolve-RefreshLocalPath -Path $WorkerDllPath -MustExist
$configuration = Resolve-RefreshLocalPath -Path $WorkerConfigPath -MustExist
$state = Resolve-RefreshLocalPath -Path $StateDirectory -Directory
$dotnetCommand = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop
$dotnet = Resolve-RefreshLocalPath -Path $dotnetCommand.Source -MustExist
if (Test-Path -LiteralPath $state -PathType Container) {
    if (@(Get-ChildItem -LiteralPath $state -Force).Count -ne 0) {
        throw 'Use a new empty runtime-state directory; existing state and logs are never overwritten.'
    }
}
if (Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction SilentlyContinue) {
    throw 'The selected task name already exists; inspect it and choose a new name rather than replacing it.'
}

$description = '{0}; StateDirectory={1}' -f (Get-RefreshTaskMarker), $state
$arguments = '{0} --worker {1} --runtime-state-dir {2}' -f `
    (Quote-RefreshArgument -Value $dll), (Quote-RefreshArgument -Value $configuration),
    (Quote-RefreshArgument -Value $state)
$action = New-ScheduledTaskAction -Execute $dotnet -Argument $arguments -WorkingDirectory (Split-Path -Parent $dll)
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 0
$principal = New-ScheduledTaskPrincipal -UserId $identity.Account -LogonType Password -RunLevel Limited
$definition = New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description $description

if ($PSCmdlet.ShouldProcess("$TaskPath$TaskName", 'Provision a protected runtime-state directory and register an explicitly identified worker task')) {
    if (-not (Test-Path -LiteralPath $state -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $state
    }
    Set-RefreshStateDirectoryAcl -Path $state -WorkerSid $identity.Sid
    $password = $Credential.GetNetworkCredential().Password
    try {
        Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -InputObject $definition `
            -User $identity.Account -Password $password -ErrorAction Stop | Out-Null
    }
    finally { $password = $null }
    if ($StartNow) {
        Start-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction Stop
    }
    [pscustomobject]@{
        TaskName = $TaskName
        TaskPath = $TaskPath
        WorkerIdentity = $identity.Account
        State = if ($StartNow) { 'start-requested' } else { 'registered-not-started' }
        StartupTrigger = 'AtStartup'
        DuplicateInstancePolicy = 'IgnoreNew'
        AutomaticRestartCount = 0
        RuntimeStateDirectory = $state
    }
}
