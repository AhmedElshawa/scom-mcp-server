Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Gui.psm1') -Force
$root=Join-Path (Split-Path $PSScriptRoot -Parent) ('.validation-data\gui-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$count=0
function Check([bool]$value) { if (-not $value) { throw 'Synthetic GUI bridge assertion failed' }; $script:count++ }
$script:effects=[Collections.Generic.List[string]]::new()
$script:ready=$true
$script:admin=$true
$script:worker='SYNTHETIC\worker'
$script:failPrepare=$false
$plan=[pscustomobject]@{
    schemaVersion=1;packagePath=(Join-Path $root 'package');installPath=(Join-Path $root 'install')
    privatePath=(Join-Path $root 'private');receiverStatePath=(Join-Path $root 'receiver');workerStatePath=(Join-Path $root 'worker')
    mcpLogPath=(Join-Path $root 'mcp-logs');collectorLogPath=(Join-Path $root 'collector-logs')
    siteName='synthetic';applicationPath='/';poolName='synthetic-pool';httpsBaseUrl='https://synthetic.invalid'
    tlsThumbprint=('A'*40);workerAccount=$script:worker;managementServer='synthetic.invalid'
    managementGroupId='22222222-2222-4222-8222-222222222222';groupId='33333333-3333-4333-8333-333333333333'
    scopeKind='group';wholeManagementGroupApproved=$false;operationsManagerModule=(Join-Path $root 'OperationsManager.psd1')
}
$request=[pscustomobject]@{action='Preflight';plan=$plan;stateDirectory=(Join-Path $root 'journal');approval=''}
$windows=@{
    Prerequisites={ [pscustomobject]@{ready=$script:ready;codes=@('SYNTHETIC');checks=@([pscustomobject]@{name='Synthetic prerequisite';passed=$script:ready})} }
    Prepare={ param($p) if ($script:failPrepare) { throw 'SETUP_SYNTHETIC_FAILURE: secret-do-not-display' }; $script:effects.Add('prepare') }
}
$adapters=@{
    Prerequisites=$windows.Prerequisites
    IsAdmin={$script:admin}
    Caller={$script:worker}
    ValidateNew={param($p) $script:effects.Add('validate-readonly')}
    Private={param($path)}
    CreateJournal={param($path) $script:effects.Add('journal');$null=New-Item -ItemType Directory -Path $path}
    Discover={param($p,$approve) & $approve 'Synthetic read-only group discovery' | Out-Null; $script:effects.Add('discover');[pscustomobject]@{Id=$p.groupId;DisplayName="Synthetic`nname"}}
    Windows={ $windows }.GetNewClosure()
    Worker={param($p,$step,$approve) & $approve "Synthetic worker $step" | Out-Null;$script:effects.Add("worker-$step")}
}
function Run { Invoke-ScomSetupGuiRequest $request $plan.packagePath -Adapters $adapters }
try {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -In @('.ps1','.psm1')) {
        $tokens=$null;$errors=$null
        $null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        Check ($errors.Count -eq 0)
    }
    $script:ready=$false
    $r=Run
    Check (-not $r.ok -and $r.code -ceq 'SETUP_PREREQUISITES_BLOCKED' -and $r.checks.Count -eq 1)
    Check ($script:effects.Count -eq 0 -and -not (Test-Path -LiteralPath $request.stateDirectory))
    $request.action='Prepare'
    $r=Run
    Check (-not $r.ok -and -not $r.approvalRequired -and $script:effects.Count -eq 0)
    $script:ready=$true
    $request.action='Preflight'
    Check (Run).ok
    Check ($script:effects.Count -eq 0)
    $request.action='Discover'
    $r=Run
    Check ($r.approvalRequired -and $script:effects.Count -eq 0)
    $request.approval='wrong approval'
    Check (Run).approvalRequired
    Check ($script:effects.Count -eq 0)
    $request.approval=$r.summary
    $r=Run
    Check ($r.ok -and $r.groups.Count -eq 1 -and $r.groups[0].displayName -ceq 'Synthetic name')
    Check ($script:effects.Count -eq 1 -and $script:effects[0] -ceq 'discover')
    $script:effects.Clear()
    $request.action='Prepare';$request.approval=''
    $script:admin=$false
    $r=Run
    Check ($r.code -ceq 'SETUP_ADMINISTRATOR_REQUIRED_FOR_HOST_LANE' -and $script:effects.Count -eq 0)
    $script:admin=$true
    $r=Run
    Check ($r.approvalRequired -and $script:effects.Count -eq 1 -and $script:effects[0] -ceq 'validate-readonly')
    Check (-not (Test-Path -LiteralPath $request.stateDirectory))
    $request.approval='cancelled'
    $r=Run
    Check ($r.approvalRequired -and -not (Test-Path -LiteralPath $request.stateDirectory))
    $request.approval=$r.summary
    $script:failPrepare=$true
    $r=Run
    Check (-not $r.ok -and $r.code -ceq 'SETUP_SYNTHETIC_FAILURE')
    Check (($r | ConvertTo-Json -Depth 10) -notmatch 'secret-do-not-display')
    $state=Get-Content -LiteralPath (Join-Path $request.stateDirectory 'setup-state.json') -Raw | ConvertFrom-Json
    Check ($state.pendingAction -ceq 'Prepare')
    $request.action='Status';$request.approval=''
    $r=Run
    Check ($r.ok -and $r.pendingAction -ceq 'Prepare' -and $r.plan.mcpLogPath -ceq $plan.mcpLogPath)
    $request.action='Prepare'
    $r=Run
    Check (-not $r.ok -and $r.code -ceq 'SETUP_INTERRUPTED_ACTION_REQUIRES_RECOVERY')
    $state.stage='prepared';$state.pendingAction=$null
    [IO.File]::WriteAllText((Join-Path $request.stateDirectory 'setup-state.json'),($state | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath (Join-Path $request.stateDirectory 'setup.lock')
    $request | Add-Member NoteProperty identityPath (Join-Path $root 'identity.json')
    $request | Add-Member NoteProperty receiverSettingsPath (Join-Path $root 'receiver.json')
    $request | Add-Member NoteProperty workerSettingsPath (Join-Path $root 'worker.json')
    $request.action='Configure';$request.approval='wrong'
    $r=Run
    Check ($r.approvalRequired -and -not (Test-Path -LiteralPath (Join-Path $request.stateDirectory 'setup.lock')))
    Check ((Get-Content -LiteralPath (Join-Path $request.stateDirectory 'setup-state.json') -Raw | ConvertFrom-Json).stage -ceq 'prepared')
    $windows.Configure={ throw 'SETUP_SYNTHETIC_CONFIGURE_FAILURE: private-marker' }
    $request.approval=$r.summary
    $r=Run
    if ($r.code -cne 'SETUP_SYNTHETIC_CONFIGURE_FAILURE') { throw "Accepted configuration returned unexpected safe code: $($r.code)" }
    Check ($r.code -ceq 'SETUP_SYNTHETIC_CONFIGURE_FAILURE' -and -not $r.ok)
    Check ((Get-Content -LiteralPath (Join-Path $request.stateDirectory 'setup-state.json') -Raw | ConvertFrom-Json).pendingAction -ceq 'Configure')
    Check (Test-Path -LiteralPath (Join-Path $request.stateDirectory 'setup.lock'))
    $request.action='Connect'
    $script:worker='SYNTHETIC\wrong'
    $r=Run
    Check ($r.code -ceq 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION')
    $script:worker=$plan.workerAccount
    $r=Run
    Check ($r.approvalRequired)
    $request.approval=$r.summary
    $r=Run
    Check ($r.ok -and $script:effects.Contains('worker-Connect'))
    $null=New-Item -ItemType Directory -Path $plan.privatePath
    $installedPlan=Join-Path $plan.privatePath 'setup-plan.json'
    [IO.File]::WriteAllText($installedPlan,($plan | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $request | Add-Member NoteProperty planPath $installedPlan
    $request.action='Status';$script:admin=$false
    $before=$script:effects.Count
    $r=Run
    Check ($r.ok -and $r.code -ceq 'SETUP_GUI_WORKER_PLAN_READY' -and $null -eq $r.stage -and $null -eq $r.pendingAction)
    Check ($r.plan.workerAccount -ceq $plan.workerAccount -and $before -eq $script:effects.Count)
    $script:worker='SYNTHETIC\wrong'
    Check ((Run).code -ceq 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION')
    $script:worker=$plan.workerAccount
    $request.PSObject.Properties.Remove('planPath')
    $request.action='Invalid'
    Check ((Run).code -ceq 'SETUP_GUI_ACTION_INVALID')
    $request.action='Preflight'
    $r=Invoke-ScomSetupGuiRequest $request (Join-Path $root 'wrong-package') -Adapters $adapters
    Check ($r.code -ceq 'SETUP_GUI_PACKAGE_LOCATION_MISMATCH')
    foreach ($inputText in @('{"action":"Invalid","secret":"synthetic-private-marker"}','synthetic-private-marker')) {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=(Get-Process -Id $PID).Path
        $start.Arguments='-NoProfile -File "'+(Join-Path $PSScriptRoot 'Invoke-ScomSetupGui.ps1')+'"'
        $start.UseShellExecute=$false
        $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $encoding=[Console]::InputEncoding
        try {
            [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
            $child=[Diagnostics.Process]::Start($start)
        } finally { [Console]::InputEncoding=$encoding }
        $child.StandardInput.WriteLine($inputText);$child.StandardInput.Close()
        $stdout=$child.StandardOutput.ReadToEnd();$stderr=$child.StandardError.ReadToEnd()
        if (-not $child.WaitForExit(30000)) { Stop-Process -Id $child.Id; throw 'GUI bridge protocol test timed out' }
        $response=$stdout | ConvertFrom-Json
        Check (-not $response.ok -and $stdout.StartsWith('{') -and $stdout.EndsWith('}'))
        Check (($stdout+$stderr) -notmatch 'synthetic-private-marker')
        $expected=if ($inputText.StartsWith('{')) {'SETUP_GUI_ACTION_INVALID'} else {'SETUP_GUI_BRIDGE_BLOCKED'}
        Check ($response.code -ceq $expected)
    }
    Write-Output "PASS: $count native GUI bridge contract assertions; mocked host/source effects only, no installation or collection."
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
