Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ScomEntra.psm1') -Force
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('.validation-data\setup-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$script:assertions = 0
function Check([bool]$Value) {
    if (-not $Value) { throw 'Synthetic setup assertion failed.' }
    $script:assertions++
}
function Reject([scriptblock]$Body, [string]$Code) {
    try { & $Body }
    catch {
        if ($_.Exception.Message -notlike "*$Code*") { throw }
        $script:assertions++
        return
    }
    throw "Expected rejection: $Code"
}
function New-Fixture {
    $path = Join-Path $root ([guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path
    $plan = [pscustomobject]@{
        schemaVersion=1; packagePath=(Join-Path $path 'package'); installPath=(Join-Path $path 'install')
        privatePath=(Join-Path $path 'private'); receiverStatePath=(Join-Path $path 'receiver'); workerStatePath=(Join-Path $path 'worker')
        mcpLogPath=(Join-Path $path 'mcp-logs'); collectorLogPath=(Join-Path $path 'collector-logs')
        siteName='synthetic-site'; applicationPath='/'; poolName='synthetic-pool'; httpsBaseUrl='https://synthetic.invalid'
        tlsThumbprint=('A' * 40); workerAccount='SYNTHETIC\worker'; managementServer='synthetic-scom.invalid'
        managementGroupId='22222222-2222-4222-8222-222222222222'; groupId='33333333-3333-4333-8333-333333333333'
        scopeKind='group'; wholeManagementGroupApproved=$false
        operationsManagerModule=(Join-Path $path 'OperationsManager.psd1')
    }
    $state = [pscustomobject]@{schemaVersion=1; stage='new'; pendingAction=$null; receiverHash=$null; workerHash=$null}
    [pscustomobject]@{plan=$plan; state=$state; statePath=(Join-Path $path 'state.json')}
}
try {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -In @('.ps1','.psm1')) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        Check ($errors.Count -eq 0)
    }
    $f = New-Fixture
    Assert-ScomSetupConnectionIdentity $f.plan @([pscustomobject]@{Id=$f.plan.managementGroupId}) @([pscustomobject]@{Id=$f.plan.groupId})
    Check $true
    Reject { Assert-ScomSetupConnectionIdentity $f.plan @() @() } 'SETUP_MANAGEMENT_GROUP_MISMATCH'
    Reject { Assert-ScomSetupConnectionIdentity $f.plan @([pscustomobject]@{Id=$f.plan.managementGroupId}) @() } 'SETUP_APPROVED_GROUP_NOT_VISIBLE'
    Reject { Assert-ScomSetupConnectionIdentity $f.plan @([pscustomobject]@{Id=$f.plan.managementGroupId}) @([pscustomobject]@{Id=[guid]::Empty}) } 'SETUP_APPROVED_GROUP_NOT_VISIBLE'
    $f = New-Fixture
    Assert-ScomSetupPlan $f.plan
    Check $true
    foreach ($bad in @('C:\','\\synthetic\share','C:\temp\..\Windows','C:\temp\wild*','relative','C:\temp:stream')) {
        Reject { Assert-ScomSetupPath $bad } 'SETUP_PATH'
    }
    $f.plan.workerStatePath = $f.plan.installPath
    Reject { Assert-ScomSetupPlan $f.plan } 'SETUP_PATH_COLLISION'
    $f = New-Fixture
    $f.plan.httpsBaseUrl='http://synthetic.invalid'
    Reject { Assert-ScomSetupPlan $f.plan } 'SETUP_HTTPS_INVALID'
    $f = New-Fixture
    $f.plan.groupId=[guid]::Empty.ToString('D')
    Reject { Assert-ScomSetupPlan $f.plan } 'SETUP_SCOPE_GUID_INVALID'
    $f = New-Fixture
    $catalog = @([pscustomobject]@{Id=$f.plan.groupId;DisplayName='Synthetic only'})
    $row = [pscustomobject]@{Id=$f.plan.groupId;DisplayName='Synthetic only'}
    $row | Add-Member ScriptMethod IsInstanceOf { param($class) $class -ceq 'synthetic-group-class' }
    $rows = @(Read-ScomSetupGroupRows @($row) 'synthetic-group-class')
    Check ($rows.Count -eq 1 -and $rows[0].Id -eq $f.plan.groupId)
    Reject { Read-ScomSetupGroupRows @($row,$row) 'synthetic-group-class' } 'SETUP_DISCOVERY_ROW_INVALID'
    Reject { Read-ScomSetupGroupRows @($row) 'wrong-class' } 'SETUP_DISCOVERY_ROW_INVALID'
    $many = @(1..201 | ForEach-Object {
        $r = [pscustomobject]@{Id=[guid]::NewGuid().ToString('D');DisplayName='Synthetic only'}
        $r | Add-Member ScriptMethod IsInstanceOf {$true}
        $r
    })
    Reject { Read-ScomSetupGroupRows $many 'synthetic-group-class' } 'SETUP_DISCOVERY_BUDGET_EXCEEDED'
    Reject { Set-ScomSetupScopeSelection $f.plan $catalog 'ALL' -Approve {$false} } 'SETUP_CANCELLED_NO_ACTION'
    Check ($f.plan.scopeKind -ceq 'group' -and -not $f.plan.wholeManagementGroupApproved)
    Set-ScomSetupScopeSelection $f.plan $catalog 'ALL' -Approve {$true}
    Assert-ScomSetupPlan $f.plan
    Check ($f.plan.scopeKind -ceq 'managementGroup' -and $null -eq $f.plan.groupId -and $f.plan.wholeManagementGroupApproved)
    Assert-ScomSetupConnectionIdentity $f.plan @([pscustomobject]@{Id=$f.plan.managementGroupId}) @()
    $f.plan.groupId=''
    Reject { Assert-ScomSetupPlan $f.plan } 'SETUP_SCOPE_APPROVAL_INVALID'
    Set-ScomSetupScopeSelection $f.plan $catalog '1'
    Assert-ScomSetupPlan $f.plan
    Check ($f.plan.groupId -eq $catalog[0].Id -and -not $f.plan.wholeManagementGroupApproved)
    Reject { Set-ScomSetupScopeSelection $f.plan $catalog '0' } 'SETUP_GROUP_SELECTION_INVALID'
    Reject { Set-ScomSetupScopeSelection $f.plan $catalog '2' } 'SETUP_GROUP_SELECTION_INVALID'
    $f.plan.mcpLogPath = $f.plan.collectorLogPath
    Reject { Assert-ScomSetupPlan $f.plan } 'SETUP_PATH_COLLISION'
    Reject { Show-ScomSetupPrerequisites ([pscustomobject]@{ready=$false;codes=@('SYNTHETIC_BLOCKER')}) } 'SETUP_PREREQUISITES_BLOCKED'

    $script:mutations = [Collections.Generic.List[string]]::new()
    $script:logEvents = [Collections.Generic.List[string]]::new()
    $adapters = @{
        Prerequisites={ param($p) [pscustomobject]@{ready=$true;codes=@()} }
        Log={ param($p,$eventName) $script:logEvents.Add($eventName) }
        Prepare={ param($p) $script:mutations.Add('Prepare') }
        Configure={
            param($p)
            $script:mutations.Add('Configure')
            $null = New-Item -ItemType Directory -Path (Join-Path $p.installPath 'server') -Force
            $null = New-Item -ItemType Directory -Path $p.privatePath -Force
            Save-ScomSetupJson (Join-Path $p.installPath 'server\appsettings.json') @{}
            Save-ScomSetupJson (Join-Path $p.privatePath 'worker.json') @{}
        }
        Validate={ param($p) $script:mutations.Add('Validate') }
        Activate={ param($p) $script:mutations.Add('Activate') }
        Publish={ param($p) $script:mutations.Add('Publish') }
        Accept={ param($p) $script:mutations.Add('Accept') }
        Health={ param($p) $script:mutations.Add('Health') }
        EnableStartup={ param($p) $script:mutations.Add('EnableStartup') }
        Stop={ param($p) $script:mutations.Add('Stop') }
    }
    $f = New-Fixture
    foreach ($action in @('Prepare','Configure','Activate','Publish','Accept')) {
        $script:mutations.Clear()
        Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath $action $adapters -Approve {$false} } 'SETUP_CANCELLED_NO_ACTION'
        Check (-not $f.state.pendingAction -and $action -notin $script:mutations)
        $null = Invoke-ScomSetupTransition $f.plan $f.state $f.statePath $action $adapters -Approve {$true}
        Check ($action -in $script:mutations -and -not $f.state.pendingAction)
        Check ((Read-ScomSetupJson $f.statePath).stage -eq $f.state.stage)
    }
    Check ($f.state.stage -eq 'accepted')
    Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath Prepare $adapters -Approve {$true} } 'SETUP_STAGE_ORDER'
    $f = New-Fixture
    $blocked = $adapters.Clone()
    $blocked.Prerequisites={ [pscustomobject]@{ready=$false;codes=@('RESTART_REQUIRED','IIS_REQUIRED')} }
    $script:mutations.Clear()
    $script:logEvents.Clear()
    Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath Prepare $blocked -Approve {$true} } 'SETUP_PREREQUISITES_BLOCKED'
    Check ($script:mutations.Count -eq 0 -and $script:logEvents.Count -eq 0 -and -not (Test-Path -LiteralPath $f.statePath))
    $logFailure=$adapters.Clone()
    $logFailure.Log={ throw 'SYNTHETIC_LOG_IO_FAILURE' }
    Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath Prepare $logFailure -Approve {$true} } 'SYNTHETIC_LOG_IO_FAILURE'
    Check ((Read-ScomSetupJson $f.statePath).pendingAction -eq 'Prepare')
    foreach ($action in @('Prepare','Configure','Activate','Publish','Accept')) {
        $f = New-Fixture
        $f.state.stage = @{Prepare='new';Configure='prepared';Activate='configured';Publish='activated';Accept='published'}[$action]
        if ($action -in @('Activate','Publish','Accept')) {
            & $adapters.Configure $f.plan
            $f.state.receiverHash=Get-ScomSetupHash (Join-Path $f.plan.installPath 'server\appsettings.json')
            $f.state.workerHash=Get-ScomSetupHash (Join-Path $f.plan.privatePath 'worker.json')
        }
        $failed = $adapters.Clone()
        $failed[$action]={ throw 'SYNTHETIC_MUTATION_FAILURE' }
        Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath $action $failed -Approve {$true} } 'SYNTHETIC_MUTATION_FAILURE'
        Check ((Read-ScomSetupJson $f.statePath).pendingAction -eq $action)
        Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath $action $adapters -Approve {$true} } 'SETUP_INTERRUPTED_ACTION_REQUIRES_RECOVERY'
    }
    $f = New-Fixture
    $f.state.stage='configured'
    & $adapters.Configure $f.plan
    $f.state.receiverHash=Get-ScomSetupHash (Join-Path $f.plan.installPath 'server\appsettings.json')
    $f.state.workerHash=Get-ScomSetupHash (Join-Path $f.plan.privatePath 'worker.json')
    $failed = $adapters.Clone()
    $failed.Health={ throw 'SYNTHETIC_HEALTH_FAILURE' }
    $script:stopAttempted=$false
    $failed.Stop={ $script:stopAttempted=$true; throw 'SYNTHETIC_ROLLBACK_FAILURE' }
    Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath Activate $failed -Approve {$true} } 'SYNTHETIC_HEALTH_FAILURE'
    Check $script:stopAttempted
    Check ((Read-ScomSetupJson $f.statePath).pendingAction -eq 'Activate')
    $f.state.pendingAction=$null
    Save-ScomSetupJson (Join-Path $f.plan.privatePath 'worker.json') @{changed=$true}
    Reject { Invoke-ScomSetupTransition $f.plan $f.state $f.statePath Activate $adapters -Approve {$true} } 'SETUP_CONFIGURATION_CHANGED'

    $identity = [pscustomobject]@{
        schemaVersion=1;tenantId='11111111-1111-4111-8111-111111111111';tokenVersion=2
        apiClientId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';resource='api://aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
        publisherClientId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';publisherObjectId='cccccccc-cccc-4ccc-8ccc-cccccccccccc'
        readerClientId='dddddddd-dddd-4ddd-8ddd-dddddddddddd';certificateThumbprint=('A'*40)
        requiredScope='Scom.Read';ingestionRole='Scom.Ingest'
    }
    Assert-ScomSetupIdentity $identity
    Check $true
    $f = New-Fixture
    $collector = Join-Path $f.plan.installPath 'collector'
    $null = New-Item -ItemType Directory -Path $collector
    [IO.File]::WriteAllText((Join-Path $collector 'Invoke-SCOMGenericGroupProfile.ps1'),'throw "never run"')
    $repo = Split-Path $PSScriptRoot -Parent
    $receiver = Read-ScomSetupJson (Join-Path $repo 'ScomMcp\appsettings.ReadModel.example.json')
    $worker = Read-ScomSetupJson (Join-Path $repo 'ScomMcp\appsettings.Worker.example.json')
    $worker.profile.configuration.managementServer=$f.plan.managementServer
    $worker.profile.configuration.operationsManagerModule=$f.plan.operationsManagerModule
    $merged = Merge-ScomSetupConfiguration $f.plan $receiver $worker $identity
    Check ($merged.receiver.Auth.Audiences[0] -eq $identity.apiClientId)
    Check ($merged.receiver.ReadModel.PublisherObjectIds[0] -eq $identity.publisherObjectId)
    Check (-not $merged.worker.profile.configuration.scopeVisibilityVerified -and -not $merged.worker.profile.configuration.membershipQueryContractVerified)
    Check (-not $merged.worker.liveChecks.enabled -and -not $merged.receiver.TargetedLive.Enabled)
    Check ($merged.receiver.OperationalLogging.directory -eq $f.plan.mcpLogPath -and
        $merged.worker.operationalLogging.directory -eq $f.plan.collectorLogPath -and
        $merged.worker.profile.configuration.operationalLogging.directory -eq $f.plan.collectorLogPath -and
        $merged.worker.profile.configuration.inventoryKind -ceq 'supportedWindows')
    Set-ScomSetupScopeSelection $f.plan @() 'ALL' -Approve {$true}
    Reject { Merge-ScomSetupConfiguration $f.plan $receiver $worker $identity } 'SETUP_REVIEWED_CONFIGURATION_SCOPE_OR_DISABLED_GUARDS'
    $receiver.ReadModel.ScopeKind='managementGroup'; $receiver.ReadModel.GroupId=$null
    $receiver.ReadModel.WindowsToolsEnabled=$true
    $worker.profile.configuration.scopeKind='managementGroup'; $worker.profile.configuration.groupId=$null
    $worker.profile.configuration.wholeManagementGroupApproved=$true
    Reject { Merge-ScomSetupConfiguration $f.plan $receiver $worker $identity } 'SETUP_REVIEWED_CONFIGURATION_SCOPE_OR_DISABLED_GUARDS'
    $worker.profile.configuration.windowsMapping=[pscustomobject]@{reviewed='synthetic-only'}
    $whole = Merge-ScomSetupConfiguration $f.plan $receiver $worker $identity
    Check ($null -eq $whole.receiver.ReadModel.GroupId -and $null -eq $whole.worker.profile.configuration.groupId -and
        -not $whole.worker.profile.configuration.enumerationQueryContractVerified)
    $worker.liveChecks.enabled=$true
    Reject { Merge-ScomSetupConfiguration $f.plan $receiver $worker $identity } 'SETUP_REVIEWED_CONFIGURATION_SCOPE_OR_DISABLED_GUARDS'
    $identity.publisherClientId=$identity.apiClientId
    Reject { Assert-ScomSetupIdentity $identity } 'SETUP_IDENTITY_RESOURCE_OR_ROLE_COLLISION'

    $f = New-Fixture
    & $adapters.Configure $f.plan
    $null = New-Item -ItemType Directory -Path $f.plan.workerStatePath
    $installed = Join-Path $f.plan.installPath 'server\never-run.dll'
    [IO.File]::WriteAllText($installed,'synthetic-never-run')
    Save-ScomSetupJson (Join-Path $f.plan.privatePath 'installed-files.json') @{
        schemaVersion=1;files=@(@{path='server\never-run.dll';sha256=(Get-ScomSetupHash $installed)})
    }
    $script:sourceRuns=0
    $sourceRun={ param($p) $script:sourceRuns++; [pscustomobject]@{exitCode=0;published=$true} }
    Reject { Invoke-ScomSetupWorkerPublication $f.plan 'SYNTHETIC\administrator' {} $sourceRun -Approve {$true} } 'SETUP_EXACT_WORKER_ACCOUNT'
    Reject { Invoke-ScomSetupWorkerPublication $f.plan $f.plan.workerAccount {throw 'SYNTHETIC_DISABLED_GUARD'} $sourceRun -Approve {$true} } 'SYNTHETIC_DISABLED_GUARD'
    Reject { Invoke-ScomSetupWorkerPublication $f.plan $f.plan.workerAccount {} $sourceRun -Approve {$false} } 'SETUP_CANCELLED_NO_ACTION'
    Check ($script:sourceRuns -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $f.plan.workerStatePath 'setup-publication-attempt.json')))
    Invoke-ScomSetupWorkerPublication $f.plan $f.plan.workerAccount {} $sourceRun -Approve {$true}
    Check ($script:sourceRuns -eq 1 -and (Read-ScomSetupJson (Join-Path $f.plan.workerStatePath 'setup-publication.json')).event -eq 'SCOM_WORKER_PUBLISHED')
    Reject { Invoke-ScomSetupWorkerPublication $f.plan $f.plan.workerAccount {} $sourceRun -Approve {$true} } 'SETUP_EXISTING_RECEIPT_REVIEW_REQUIRED'
    Check ($script:sourceRuns -eq 1)
    [IO.File]::WriteAllText($installed,'changed-package')
    Reject { Assert-ScomSetupInstalledFiles $f.plan } 'SETUP_INSTALLED_PACKAGE_CHANGED'

    # Graph transport below is entirely in-memory. Never import Graph or create
    # a machine-store certificate in synthetic validation.
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Synthetic setup validation', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddDays(1))
    $script:apps=@{}; $script:sps=@{}; $script:assignments=@(); $script:graphWrites=0
    $graph = {
        param($method,$path,$body)
        if ($method -eq 'POST') {
            $script:graphWrites++
            if ($path -eq '/applications') {
                $app = ($body | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
                $app | Add-Member id ([guid]::NewGuid().ToString('D'))
                $app | Add-Member appId ([guid]::NewGuid().ToString('D'))
                if (-not $app.PSObject.Properties['identifierUris']) { $app | Add-Member identifierUris @() }
                $script:apps[$app.id]=$app
                return $app
            }
            if ($path -eq '/servicePrincipals') {
                $app=@($script:apps.Values | Where-Object appId -EQ $body.appId)[0]
                $sp=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');appId=$body.appId;appRoles=@()}
                if ($app.PSObject.Properties['appRoles']) { $sp.appRoles=$app.appRoles }
                $script:sps[$sp.id]=$sp
                return $sp
            }
            if ($path -match '/appRoleAssignments$') {
                $assignment=[pscustomobject]@{id=[guid]::NewGuid().ToString('D');resourceId=$body.resourceId;appRoleId=$body.appRoleId}
                $script:assignments+=@($assignment)
                return $assignment
            }
        }
        if ($method -eq 'PATCH' -and $path -match '^/applications/([^?]+)$') {
            $script:graphWrites++
            $script:apps[$matches[1]].identifierUris=$body.identifierUris
            return
        }
        if ($path -match "^/applications\?.*displayName eq '([^']+)'") {
            return [pscustomobject]@{value=@($script:apps.Values | Where-Object displayName -EQ $matches[1])}
        }
        if ($path -match '^/applications/([^?]+)') { return $script:apps[$matches[1]] }
        if ($path -match "^/servicePrincipals\?.*appId eq '([^']+)'") {
            return [pscustomobject]@{value=@($script:sps.Values | Where-Object appId -EQ $matches[1])}
        }
        if ($path -match '/appRoleAssignments\?') { return [pscustomobject]@{value=$script:assignments} }
        if ($path -match '^/servicePrincipals/([^?]+)') { return $script:sps[$matches[1]] }
        throw "Unexpected synthetic Graph call $method $path"
    }
    $journal = Join-Path $root 'entra.json'
    Reject { Invoke-ScomEntraProvisioning -TenantId '11111111-1111-4111-8111-111111111111' -NamePrefix synthetic -Certificate $certificate -JournalPath $journal -Graph $graph -Approve {$false} } 'ENTRA_CANCELLED_NO_MUTATION'
    Check ($script:graphWrites -eq 0)
    $result = Invoke-ScomEntraProvisioning -TenantId '11111111-1111-4111-8111-111111111111' -NamePrefix synthetic -Certificate $certificate -JournalPath $journal -Graph $graph -Approve {$true} -GrantPublisherRole
    Check ($script:graphWrites -eq 8 -and $result.publisherObjectId -ne $result.publisherClientId)
    Check (@($script:apps.Values | Where-Object { $_.PSObject.Properties['passwordCredentials'] }).Count -eq 0)
    $apiId=@($script:apps.Values | Where-Object displayName -EQ 'synthetic-api')[0].id
    $publisherId=@($script:apps.Values | Where-Object displayName -EQ 'synthetic-publisher')[0].id
    $readerId=@($script:apps.Values | Where-Object displayName -EQ 'synthetic-reader')[0].id
    $arguments=@{
        TenantId='11111111-1111-4111-8111-111111111111';NamePrefix='synthetic';Certificate=$certificate;JournalPath=$journal
        Graph=$graph;Approve={$true};ApiObjectId=$apiId;PublisherObjectId=$publisherId;ReaderObjectId=$readerId
    }
    $repeat=Invoke-ScomEntraProvisioning @arguments
    Check ($script:graphWrites -eq 8 -and $repeat.resource -eq $result.resource)
    $script:apps[$apiId].api.requestedAccessTokenVersion=1
    Reject { Invoke-ScomEntraProvisioning @arguments } 'ENTRA_EXISTING_API_CONTRACT_CONFLICT_NO_OVERWRITE'
    Check ($script:graphWrites -eq 8)
    $script:apps[$apiId].api.requestedAccessTokenVersion=2
    $script:assignments=@()
    Reject { Invoke-ScomEntraProvisioning @arguments } 'ENTRA_PUBLISHER_CONSENT_REQUIRED_NO_READY_OUTPUT'
    Check ($script:graphWrites -eq 8)
    $record=Read-ScomSetupJson $journal
    $record.pending=[pscustomobject]@{path='/applications';effect='Interrupted synthetic write'}
    Save-ScomSetupJson $journal $record
    Reject { Invoke-ScomEntraProvisioning @arguments } 'ENTRA_INTERRUPTED_MUTATION_REVIEW_GRAPH_AND_JOURNAL_NO_BLIND_RETRY'
    Check ($script:graphWrites -eq 8)
    foreach ($failAt in 1..8) {
        $script:apps=@{}; $script:sps=@{}; $script:assignments=@(); $script:graphWrites=0
        $control=[pscustomobject]@{calls=0;failAt=$failAt}
        $failingGraph={
            param($method,$path,$body)
            if ($method -in @('POST','PATCH')) {
                $control.calls++
                if ($control.calls -eq $control.failAt) { throw 'SYNTHETIC_GRAPH_MUTATION_FAILURE' }
            }
            & $graph $method $path $body
        }.GetNewClosure()
        $failureJournal=Join-Path $root "entra-failure-$failAt.json"
        $failureArguments=@{
            TenantId='11111111-1111-4111-8111-111111111111';NamePrefix='synthetic';Certificate=$certificate;JournalPath=$failureJournal
            Graph=$failingGraph;Approve={$true};GrantPublisherRole=$true
        }
        Reject { Invoke-ScomEntraProvisioning @failureArguments } 'SYNTHETIC_GRAPH_MUTATION_FAILURE'
        Check ([bool](Read-ScomSetupJson $failureJournal).pending)
        Reject { Invoke-ScomEntraProvisioning @failureArguments } 'ENTRA_INTERRUPTED_MUTATION'
        Check ($control.calls -eq $failAt)
    }
    $certificate.Dispose(); $rsa.Dispose()
    Write-Output "PASS: $script:assertions synthetic setup/provisioning assertions; no IIS, SCOM, Graph, installs or machine-certificate writes."
}
finally {
    # Only this exact newly created fixture directory is removed.
    Remove-Item -LiteralPath $root -Recurse -Force
}
