Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-ScomSetupJson {
    param([Parameter(Mandatory)][string]$Path)
    Assert-ScomSetupPath $Path
    if (-not [IO.File]::Exists($Path) -or (Get-Item -LiteralPath $Path).Length -gt 1MB) {
        throw 'SETUP_JSON_MISSING_OR_TOO_LARGE'
    }
    Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
}

function Assert-ScomSetupPath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path -notmatch '^[A-Za-z]:\\' -or $Path -match '[*?"<>|]' -or $Path.Substring(2).Contains(':') -or
        $Path -match '\\\.\.?($|\\)' -or $Path -match '[. ]($|\\)' -or
        $Path -match '(?i)\\(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\\|$)' -or
        $Path.TrimEnd('\') -eq [IO.Path]::GetPathRoot($Path).TrimEnd('\')) {
        throw 'SETUP_PATH_REQUIRES_NONROOT_LOCAL_ABSOLUTE_PATH'
    }
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($Path)).DriveType -ne 'Fixed') {
        throw 'SETUP_PATH_REQUIRES_FIXED_LOCAL_DRIVE'
    }
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'SETUP_PATH_REPARSE_POINT'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Assert-ScomSetupPlan {
    param([Parameter(Mandatory)]$Plan)
    if ($Plan.schemaVersion -ne 1) { throw 'SETUP_PLAN_VERSION' }
    $paths = @($Plan.packagePath, $Plan.installPath, $Plan.privatePath, $Plan.receiverStatePath, $Plan.workerStatePath,
        $Plan.mcpLogPath, $Plan.collectorLogPath)
    foreach ($path in $paths) { Assert-ScomSetupPath $path }
    foreach ($path in @($Plan.mcpLogPath,$Plan.collectorLogPath)) {
        if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($path)).DriveFormat -cne 'NTFS') { throw 'SETUP_LOG_NTFS_REQUIRED' }
    }
    foreach ($target in @($Plan.installPath,$Plan.privatePath,$Plan.receiverStatePath,$Plan.workerStatePath,$Plan.mcpLogPath,$Plan.collectorLogPath)) {
        $windows = [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\') + '\'
        if (([IO.Path]::GetFullPath($target).TrimEnd('\') + '\').StartsWith($windows,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'SETUP_TARGET_MUST_NOT_BE_WINDOWS_DIRECTORY'
        }
    }
    for ($i = 0; $i -lt $paths.Count; $i++) {
        for ($j = $i + 1; $j -lt $paths.Count; $j++) {
            $a = [IO.Path]::GetFullPath($paths[$i]).TrimEnd('\') + '\'
            $b = [IO.Path]::GetFullPath($paths[$j]).TrimEnd('\') + '\'
            if ($a.StartsWith($b, [StringComparison]::OrdinalIgnoreCase) -or
                $b.StartsWith($a, [StringComparison]::OrdinalIgnoreCase)) { throw 'SETUP_PATH_COLLISION' }
        }
    }
    foreach ($name in @($Plan.siteName, $Plan.poolName)) {
        if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$') { throw 'SETUP_IIS_NAME_INVALID' }
    }
    if ($Plan.applicationPath -notmatch '^/([A-Za-z0-9_-]+)?$') { throw 'SETUP_APPLICATION_PATH_INVALID' }
    $uri = [uri]$Plan.httpsBaseUrl
    if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.HostNameType -ne 'Dns' -or $uri.AbsolutePath.TrimEnd('/') -ne $Plan.applicationPath.TrimEnd('/') -or
        $Plan.tlsThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'SETUP_HTTPS_INVALID' }
    if ($Plan.scopeKind -cnotin @('group','managementGroup') -or
        $Plan.wholeManagementGroupApproved -isnot [bool] -or
        ($Plan.scopeKind -ceq 'managementGroup' -and (-not $Plan.wholeManagementGroupApproved -or $null -ne $Plan.groupId)) -or
        ($Plan.scopeKind -ceq 'group' -and $Plan.wholeManagementGroupApproved)) { throw 'SETUP_SCOPE_APPROVAL_INVALID' }
    $ids = @($Plan.managementGroupId)
    if ($Plan.scopeKind -ceq 'group') { $ids += $Plan.groupId }
    foreach ($id in $ids) {
        $parsed = [guid]::Empty
        if (-not [guid]::TryParseExact($id, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'SETUP_SCOPE_GUID_INVALID' }
    }
    if ($Plan.managementServer -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$' -or
        $Plan.workerAccount -notmatch '^[^\\\s]+\\[^\\\s]+$') { throw 'SETUP_SOURCE_IDENTITY_INVALID' }
    Assert-ScomSetupPath $Plan.operationsManagerModule
    if ($Plan.workerAccount -eq "IIS APPPOOL\$($Plan.poolName)") { throw 'SETUP_IDENTITIES_MUST_BE_SEPARATE' }
}

function Get-ScomSetupHash {
    param([Parameter(Mandatory)][string]$Path)
    Assert-ScomSetupPath $Path
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-ScomSetupScopeDescription {
    param($Plan)
    if ($Plan.scopeKind -ceq 'managementGroup') { return 'all supported Windows inventory in the selected management group (not every SCOM class)' }
    "existing approved group $($Plan.groupId)"
}

function Save-ScomSetupJson {
    param([string]$Path, $Value)
    Assert-ScomSetupPath $Path
    $temporary = "$Path.new"
    if (Test-Path -LiteralPath $temporary) { throw 'SETUP_INTERRUPTED_STATE_WRITE_REQUIRES_REVIEW' }
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 48), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) {
        [IO.File]::Replace($temporary, $Path, [Management.Automation.Language.NullString]::Value)
    } else { [IO.File]::Move($temporary, $Path) }
}

function Assert-ScomSetupInstalledFiles {
    param($Plan)
    $manifest = Read-ScomSetupJson (Join-Path $Plan.privatePath 'installed-files.json')
    if ($manifest.schemaVersion -ne 1 -or $manifest.files.Count -eq 0 -or $manifest.files.Count -gt 1000) {
        throw 'SETUP_INSTALLED_MANIFEST_INVALID'
    }
    foreach ($file in $manifest.files) {
        if ($file.path -notmatch '^(server|collector)\\' -or $file.path -match '(^|\\)\.\.?($|\\)' -or
            (Get-ScomSetupHash (Join-Path $Plan.installPath $file.path)) -ne $file.sha256) {
            throw 'SETUP_INSTALLED_PACKAGE_CHANGED'
        }
    }
}

function Invoke-ScomSetupWorkerPublication {
    param(
        $Plan, [string]$CallerName, [scriptblock]$Validate, [scriptblock]$RunOnce,
        [scriptblock]$Approve = ${function:Confirm-ScomSetupEffect}
    )
    Assert-ScomSetupPlan $Plan
    if ($CallerName -ne $Plan.workerAccount) { throw 'SETUP_EXACT_WORKER_ACCOUNT_REQUIRED_NO_IMPERSONATION' }
    Assert-ScomSetupInstalledFiles $Plan
    & $Validate $Plan | Out-Null
    if (-not (& $Approve "ONE bounded collection/publication/checkpoint attempt for $(Get-ScomSetupScopeDescription $Plan), as $($Plan.workerAccount), to $($Plan.httpsBaseUrl). Uses reviewed worker deadlines. No recurrence, metrics or live checks. Existing pending dataset can resume under the same policy.")) {
        throw 'SETUP_CANCELLED_NO_ACTION'
    }
    $lease = [IO.File]::Open((Join-Path $Plan.workerStatePath 'setup-worker.lock'), 'OpenOrCreate','ReadWrite','None')
    try {
        $receipt = Join-Path $Plan.workerStatePath 'setup-publication.json'
        if (Test-Path -LiteralPath $receipt) { throw 'SETUP_EXISTING_RECEIPT_REVIEW_REQUIRED_NO_IMPLICIT_RERUN' }
        $worker = Join-Path $Plan.privatePath 'worker.json'
        $receiver = Join-Path $Plan.installPath 'server\appsettings.json'
        $workerHash = Get-ScomSetupHash $worker
        $receiverHash = Get-ScomSetupHash $receiver
        # Journal before invocation: a crash after commit must not cause an
        # automatic source read/publication when setup resumes.
        $attempt = Join-Path $Plan.workerStatePath 'setup-publication-attempt.json'
        if (Test-Path -LiteralPath $attempt) { throw 'SETUP_INTERRUPTED_PUBLICATION_REQUIRES_CHECKPOINT_REVIEW' }
        Save-ScomSetupJson $attempt @{schemaVersion=1; workerHash=$workerHash; receiverHash=$receiverHash}
        $result = & $RunOnce $Plan
        if ($result.exitCode -ne 0 -or -not $result.published -or
            $workerHash -ne (Get-ScomSetupHash $worker) -or $receiverHash -ne (Get-ScomSetupHash $receiver)) {
            throw 'SETUP_PUBLICATION_NOT_ACKNOWLEDGED_OR_CONFIGURATION_CHANGED'
        }
        Save-ScomSetupJson $receipt ([ordered]@{
            schemaVersion=1; event='SCOM_WORKER_PUBLISHED'; workerAccount=$Plan.workerAccount
            workerHash=$workerHash; receiverHash=$receiverHash; completedUtc=[DateTime]::UtcNow.ToString('o')
        })
    }
    finally { $lease.Dispose() }
}

function Confirm-ScomSetupEffect {
    param([string]$Summary)
    Write-Host $Summary
    (Read-Host 'Type APPROVE to perform exactly this action; any other answer cancels') -ceq 'APPROVE'
}

function Assert-ScomSetupConnectionIdentity {
    param($Plan, [object[]]$ManagementGroups, [object[]]$Groups)
    if ($ManagementGroups.Count -ne 1 -or [string]$ManagementGroups[0].Id -ne $Plan.managementGroupId) {
        throw 'SETUP_MANAGEMENT_GROUP_MISMATCH'
    }
    if ($Plan.scopeKind -ceq 'group' -and ($Groups.Count -ne 1 -or [string]$Groups[0].Id -ne $Plan.groupId)) {
        throw 'SETUP_APPROVED_GROUP_NOT_VISIBLE'
    }
}

function Set-ScomSetupScopeSelection {
    param($Plan, [object[]]$Groups, [string]$Selection, [scriptblock]$Approve = ${function:Confirm-ScomSetupEffect})
    if ($Groups.Count -gt 200) { throw 'SETUP_DISCOVERY_BUDGET_EXCEEDED' }
    if ($Selection -ceq 'ALL') {
        if (-not (& $Approve 'WARNING: broader scope. Include ALL supported Windows inventory in this management group, not every SCOM class. This may greatly increase load and exposure. Separately approve this scope; collection remains disabled until its own approval.')) {
            throw 'SETUP_CANCELLED_NO_ACTION'
        }
        $Plan.scopeKind = 'managementGroup'
        $Plan.groupId = $null
        $Plan.wholeManagementGroupApproved = $true
    } else {
        $index = 0
        if (-not [int]::TryParse($Selection, [ref]$index) -or $index -lt 1 -or $index -gt $Groups.Count) {
            throw 'SETUP_GROUP_SELECTION_INVALID'
        }
        $id = [guid]::Empty
        if (-not [guid]::TryParseExact([string]$Groups[$index - 1].Id, 'D', [ref]$id) -or $id -eq [guid]::Empty) {
            throw 'SETUP_SCOPE_GUID_INVALID'
        }
        $Plan.scopeKind = 'group'
        $Plan.groupId = $id.ToString('D')
        $Plan.wholeManagementGroupApproved = $false
    }
}

function Read-ScomSetupGroupRows {
    param([Parameter(Mandatory)]$Reader, [Parameter(Mandatory)]$GroupClass)
    $rows = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[guid]]::new()
    foreach ($row in $Reader) {
        if ($rows.Count -ge 200) { throw 'SETUP_DISCOVERY_BUDGET_EXCEEDED_USE_NARROWER_REVIEWED_SCOPE' }
        $id = [guid]$row.Id
        if ($id -eq [guid]::Empty -or -not $seen.Add($id) -or -not $row.IsInstanceOf($GroupClass)) {
            throw 'SETUP_DISCOVERY_ROW_INVALID'
        }
        $rows.Add([pscustomobject]@{Id=$id.ToString('D'); DisplayName=[string]$row.DisplayName})
    }
    $rows.ToArray() | Sort-Object DisplayName,Id
}

function Show-ScomSetupPrerequisites {
    param($Result)
    Write-Host 'PREFLIGHT ONLY - no prerequisite or application installation, task registration, or inventory collection.'
    if ($Result.PSObject.Properties['checks']) {
        foreach ($check in $Result.checks) {
            $label = if ($check.passed) { 'PASS' } else { 'BLOCKED' }
            Write-Host "$label`: $($check.name)"
        }
    }
    if ($Result.ready) { Write-Host 'PASS: all required prerequisite checks passed. Installation still requires APPROVE.' }
    else {
        foreach ($code in $Result.codes) { Write-Host "BLOCKED: $code" }
        throw 'SETUP_PREREQUISITES_BLOCKED'
    }
}

function Invoke-ScomSetupTransition {
    param(
        [Parameter(Mandatory)]$Plan, [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][string]$Action,
        [Parameter(Mandatory)][hashtable]$Adapters,
        [scriptblock]$Approve = ${function:Confirm-ScomSetupEffect},
        $InputSettings
    )
    Assert-ScomSetupPlan $Plan
    if ($State.pendingAction) { throw 'SETUP_INTERRUPTED_ACTION_REQUIRES_RECOVERY' }
    $expected = @{ Prepare='new'; Configure='prepared'; Activate='configured'; Publish='activated'; Accept='published' }
    if (-not $expected.ContainsKey($Action) -or
        ($State.stage -ne $expected[$Action] -and -not ($Action -eq 'Configure' -and $State.stage -eq 'configured'))) {
        throw 'SETUP_STAGE_ORDER'
    }
    $prerequisites = & $Adapters.Prerequisites $Plan
    if (-not $prerequisites.ready) { throw "SETUP_PREREQUISITES_BLOCKED: $($prerequisites.codes -join ', ')" }
    if ($Action -in @('Activate','Publish','Accept')) {
        if ((Get-ScomSetupHash (Join-Path $Plan.installPath 'server\appsettings.json')) -ne $State.receiverHash -or
            (Get-ScomSetupHash (Join-Path $Plan.privatePath 'worker.json')) -ne $State.workerHash) {
            throw 'SETUP_CONFIGURATION_CHANGED_REVIEW_REQUIRED'
        }
        & $Adapters.Validate $Plan | Out-Null
    }
    $summaries = @{
        Prepare="Create ONLY new installation $($Plan.installPath), private config $($Plan.privatePath), receiver state $($Plan.receiverStatePath), worker state $($Plan.workerStatePath), MCP logs $($Plan.mcpLogPath) and collector logs $($Plan.collectorLogPath). Copy reviewed package; restrict ACLs. No IIS startup/source queries."
        Configure='Back up staged configuration privately and import reviewed receiver/worker JSON plus nonsecret identity. No source verification flags will be set by setup.'
        Activate="Create dedicated pool $($Plan.poolName) and HTTPS site/application $($Plan.siteName)$($Plan.applicationPath), using $($Plan.httpsBaseUrl). No existing app/pool is overwritten. Start only this app; enable normal IIS startup after HTTPS health succeeds. Stop and disable ONLY its owned pool on failure."
        Publish="Record the worker-reported publication/checkpoint receipt for $(Get-ScomSetupScopeDescription $Plan). No new source run: use the separate explicitly approved worker lane first. Receipt alone is not reader acceptance."
        Accept='Record operator attestation of intended-user Studio tool discovery, committed inventory, unauthorized-user denial and scoped coverage. This is NOT automated certification.'
    }
    if (-not (& $Approve $summaries[$Action])) { throw 'SETUP_CANCELLED_NO_ACTION' }
    $State.pendingAction = $Action
    Save-ScomSetupJson $StatePath $State
    try {
        if ($Action -ne 'Prepare' -and $Adapters.ContainsKey('Log')) { & $Adapters.Log $Plan 'setup.started' | Out-Null }
        & $Adapters[$Action] $Plan $InputSettings | Out-Null
        if ($Action -eq 'Activate') {
            & $Adapters.Health $Plan | Out-Null
            & $Adapters.EnableStartup $Plan | Out-Null
        }
        if ($Action -eq 'Configure') {
            $State.receiverHash = Get-ScomSetupHash (Join-Path $Plan.installPath 'server\appsettings.json')
            $State.workerHash = Get-ScomSetupHash (Join-Path $Plan.privatePath 'worker.json')
        }
        if ($Adapters.ContainsKey('Log')) { & $Adapters.Log $Plan 'setup.completed' | Out-Null }
        $State.stage = @{Prepare='prepared'; Configure='configured'; Activate='activated'; Publish='published'; Accept='accepted'}[$Action]
        $State.pendingAction = $null
        Save-ScomSetupJson $StatePath $State
    }
    catch {
        # Keep the pending action durable even when stopping the owned app fails.
        # Never claim rollback succeeded or automatically repeat a publication.
        Write-Warning "SETUP_ACTION_FAILED: $Action. Pending journal retained; inspect private state before recovery."
        if ($Adapters.ContainsKey('Log')) {
            try { & $Adapters.Log $Plan 'setup.failed' | Out-Null }
            catch { Write-Warning 'SETUP_OPERATIONAL_LOG_FAILED. Pending journal retained; inspect log permissions and capacity.' }
        }
        if ($Action -eq 'Activate') {
            try { & $Adapters.Stop $Plan | Out-Null }
            catch { Write-Warning 'SETUP_STOP_FAILED. Application may still be active; operator recovery required. Journal is NOT cleared.' }
        }
        throw
    }
    $State
}

function Assert-ScomSetupIdentity {
    param($Identity)
    if ($Identity.schemaVersion -ne 1 -or $Identity.tokenVersion -ne 2 -or
        $Identity.requiredScope -cne 'Scom.Read' -or $Identity.ingestionRole -cne 'Scom.Ingest' -or
        $Identity.certificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'SETUP_IDENTITY_INVALID' }
    foreach ($name in @('tenantId','apiClientId','publisherClientId','publisherObjectId','readerClientId')) {
        $id = [guid]::Empty
        if (-not [guid]::TryParseExact($Identity.$name, 'D', [ref]$id) -or $id -eq [guid]::Empty) {
            throw 'SETUP_IDENTITY_GUID_INVALID'
        }
    }
    if ($Identity.apiClientId -eq $Identity.publisherClientId -or $Identity.apiClientId -eq $Identity.readerClientId -or
        $Identity.publisherClientId -eq $Identity.readerClientId -or $Identity.resource -cne "api://$($Identity.apiClientId)") {
        throw 'SETUP_IDENTITY_RESOURCE_OR_ROLE_COLLISION'
    }
    # Shape validation is not ownership, key access, consent or tenant attestation.
}

function Merge-ScomSetupConfiguration {
    param($Plan, $Receiver, $Worker, $Identity)
    Assert-ScomSetupIdentity $Identity
    if ($Receiver.ReadModel.ScopeKind -cne $Plan.scopeKind -or $Worker.profile.configuration.scopeKind -cne $Plan.scopeKind -or
        $Receiver.ReadModel.ManagementGroupId -ne $Plan.managementGroupId -or
        $Worker.profile.configuration.managementGroupId -ne $Plan.managementGroupId -or
        $Receiver.ReadModel.GroupId -ne $Plan.groupId -or $Worker.profile.configuration.groupId -ne $Plan.groupId -or
        $Worker.profile.configuration.managementServer -ne $Plan.managementServer -or
        $Worker.profile.configuration.operationsManagerModule -ne $Plan.operationsManagerModule -or
        $Receiver.Source.SnapshotEnabled -ne $false -or $Receiver.ReadModel.Enabled -ne $true -or
        $Receiver.TargetedLive.Enabled -ne $false -or $Worker.liveChecks.enabled -ne $false -or
        @($Receiver.ReadModel.AllowedMetricsByClass.PSObject.Properties).Count -ne 0 -or
        @($Worker.profile.configuration.metricMappingsByClass.PSObject.Properties).Count -ne 0 -or
        $Worker.profile.configuration.wholeManagementGroupApproved -ne $Plan.wholeManagementGroupApproved -or
        ($Plan.scopeKind -ceq 'managementGroup' -and
            ($Receiver.ReadModel.WindowsToolsEnabled -ne $true -or $null -eq $Worker.profile.configuration.windowsMapping))) {
        throw 'SETUP_REVIEWED_CONFIGURATION_SCOPE_OR_DISABLED_GUARDS'
    }
    if ($Receiver.Auth.Callers.Count -eq 0 -or @($Receiver.Auth.Callers | Where-Object {
        -not $_.ObjectId -or $_.Scopes.Count -ne 1 -or $_.Scopes[0] -cne 'scom-lab'
    }).Count -ne 0) { throw 'SETUP_IMMUTABLE_READER_GRANTS_REQUIRED' }
    $Receiver.Auth.TenantId = $Identity.tenantId
    $Receiver.Auth.Audiences = @($Identity.apiClientId)
    $Receiver.Auth.RequiredScope = $Identity.requiredScope
    $Receiver.ReadModel.PublisherObjectIds = @($Identity.publisherObjectId)
    $Receiver.ReadModel.IngestionRole = $Identity.ingestionRole
    $Receiver.ReadModel.DatabasePath = Join-Path $Plan.receiverStatePath 'read-model.db'
    $Receiver.AllowedHosts = ([uri]$Plan.httpsBaseUrl).DnsSafeHost
    $Worker.checkpointPath = Join-Path $Plan.workerStatePath 'worker.db'
    $Worker.identity.tenantId = $Identity.tenantId
    $Worker.identity.clientId = $Identity.publisherClientId
    $Worker.identity.resource = $Identity.resource
    $Worker.identity.certificateThumbprint = $Identity.certificateThumbprint
    $Worker.publisher.endpoint = $Plan.httpsBaseUrl.TrimEnd('/')
    $Worker.profile.scriptPath = Join-Path $Plan.installPath 'collector\Invoke-SCOMGenericGroupProfile.ps1'
    $Worker.profile.scriptSha256 = Get-ScomSetupHash $Worker.profile.scriptPath
    $Worker.profile.executablePath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $Receiver | Add-Member -Force NoteProperty OperationalLogging ([pscustomobject]@{
        directory=$Plan.mcpLogPath; maxFileBytes=1048576; retainedFiles=5
    })
    $Worker | Add-Member -Force NoteProperty operationalLogging ([pscustomobject]@{
        directory=$Plan.collectorLogPath; maxFileBytes=1048576; retainedFiles=5
    })
    $Worker.profile.configuration | Add-Member -Force NoteProperty operationalLogging $Worker.operationalLogging
    $Worker.profile.configuration | Add-Member -Force NoteProperty inventoryKind 'supportedWindows'
    [pscustomobject]@{ receiver=$Receiver; worker=$Worker }
}

Export-ModuleMember -Function *-ScomSetup*
