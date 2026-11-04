$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ScomOperationalLogging.psm1') -Force
$directory=Join-Path (Get-Location).Path ('.collector-log-validation-'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $directory
$junction=$directory+'-junction'
function Assert-LogFault([scriptblock]$Action) {
    $failed=$false
    try { & $Action | Out-Null } catch { $failed=$true }
    if (-not $failed) {throw 'Expected fail-closed logging rejection.'}
}
try {
    $configuration=[pscustomobject]@{
        directory=$directory;maxFileBytes=1024;retainedFiles=3
        token='DO_NOT_LOG_CUSTOMER_TOKEN';id=[Guid]::NewGuid().ToString()
    }
    $log=New-SCOMOperationalLog $configuration
    $captured=@(1..100 | ForEach-Object { Write-SCOMOperationalEvent -Log $log -Event 'scan.completed' })
    if ($captured.Count -ne 0) {throw 'Operational logger wrote to stdout.'}
    $files=@(Get-ChildItem -LiteralPath $directory)
    if ($files.Count -ne 3 -or @($files | Where-Object Length -GT 1024).Count -ne 0) {
        throw 'Log rotation exceeded count or byte budget.'
    }
    foreach ($file in $files) {
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $record=$line | ConvertFrom-Json
            $keys=@($record.PSObject.Properties.Name | Sort-Object)
            if (($keys -join ',') -cne 'component,event,schemaVersion,timestampUtc' -or
                $record.schemaVersion -ne 1 -or $record.component -cne 'collectorProfile' -or
                $record.event -cne 'scan.completed' -or $line.Contains($directory) -or
                $line.Contains($configuration.id) -or $line.Contains('DO_NOT_LOG_CUSTOMER_TOKEN')) {
                throw 'Logger schema leaked configuration or non-allowlisted data.'
            }
        }
    }
    foreach ($event in @('setup.started','setup.completed','setup.failed')) {
        $captured=@(Write-SCOMOperationalEvent -Log $log -Event $event)
        $setupText=@(Get-Content -LiteralPath (Join-Path $directory 'scom-collector-profile.jsonl'))[-1]
        $setupLine=$setupText | ConvertFrom-Json
        $keys=@($setupLine.PSObject.Properties.Name | Sort-Object)
        if ($captured.Count -ne 0 -or $setupLine.component -cne 'setup' -or
            $setupLine.event -cne $event -or $setupLine.schemaVersion -ne 1 -or
            ($keys -join ',') -cne 'component,event,schemaVersion,timestampUtc' -or
            $setupText.Contains($directory) -or $setupText.Contains($configuration.id) -or
            $setupText.Contains('DO_NOT_LOG_CUSTOMER_TOKEN')) {
            throw 'Reusable Setup fixed-event contract, sanitization or stdout purity failed.'
        }
    }
    if (@(Get-ChildItem -LiteralPath $directory | Where-Object {
        $_.Name -notmatch '^scom-collector-profile\.jsonl(?:\.[12])?$'
    }).Count -ne 0) {
        throw 'Exclusive Setup transitions must reuse the collector stream, not the worker file.'
    }
    Assert-LogFault { Write-SCOMOperationalEvent -Log $log -Event 'setup.DO_NOT_LOG_CUSTOMER_TOKEN' }
    Assert-LogFault { Write-SCOMOperationalEvent -Log $log -Event 'DO_NOT_LOG_CUSTOMER_TOKEN' }
    foreach ($bad in @('\\server\share','C:\missing-scom-log-directory\child',"$directory\..\escape","$directory\ads:stream")) {
        Assert-LogFault {New-SCOMOperationalLog ([pscustomobject]@{directory=$bad})}
    }
    $configuration.maxFileBytes=1
    Assert-LogFault {New-SCOMOperationalLog $configuration}
    $configuration.maxFileBytes=1024;$configuration.retainedFiles=21
    Assert-LogFault {New-SCOMOperationalLog $configuration}
    $path=Join-Path $directory 'scom-collector-profile.jsonl'
    $lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { Assert-LogFault {Write-SCOMOperationalEvent -Log $log -Event 'profile.failed'} }
    finally {$lock.Dispose()}
    $null=New-Item -ItemType Junction -Path $junction -Target $directory
    Assert-LogFault {New-SCOMOperationalLog ([pscustomobject]@{directory=$junction})}
    $nested=Join-Path $directory 'nested'
    $null=New-Item -ItemType Directory -Path $nested
    Assert-LogFault {New-SCOMOperationalLog ([pscustomobject]@{directory=(Join-Path $junction 'nested')})}
    $configuration.retainedFiles=3
    # No SCOM module is loaded: invalid scope fails before any source operation.
    $inputData=@{
        protocolVersion=1;configuration=@{
            inventoryKind='supportedWindows';operationalLogging=$configuration
            windowsMapping=@{verified=$true};scopeKind='DO_NOT_LOG_CUSTOMER_TOKEN';scopeVisibilityVerified=$true
        };request=@{}
    } | ConvertTo-Json -Depth 6 -Compress
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=(Get-Process -Id $PID).Path
    $start.Arguments='-NoProfile -File "'+(Join-Path $PSScriptRoot 'Invoke-SCOMGenericGroupProfile.ps1')+'"'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=New-Object Diagnostics.Process
    $process.StartInfo=$start
    try {
        $null=$process.Start()
        $process.StandardInput.Write($inputData);$process.StandardInput.Close()
        $stdout=$process.StandardOutput.ReadToEnd();$stderr=$process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 1 -or $stdout -cne '' -or $stderr -cne 'SCOM_GENERIC_PROFILE_FAILED') {
            throw 'Profile failure polluted stdout or leaked sensitive diagnostics.'
        }
    }
    finally {$process.Dispose()}
    if ($PSVersionTable.PSEdition -ne 'Desktop') {
        # Core rejects the Desktop-only profile before logging. Exercise the
        # identical two-event rotation boundary without an SDK or policy change.
        Write-SCOMOperationalEvent -Log $log -Event 'profile.started'
        Write-SCOMOperationalEvent -Log $log -Event 'profile.failed'
    }
    # The preceding rotation exercise leaves 854 bytes active: started fits,
    # but failed rotates it. Lifecycle evidence spans the retained stream.
    $events=@(
        for ($i=$log.RetainedFiles-1; $i -ge 0; $i--) {
            $candidate=$path
            if ($i -gt 0) { $candidate="$path.$i" }
            if (Test-Path -LiteralPath $candidate) {
                Get-Content -LiteralPath $candidate | ForEach-Object {($_ | ConvertFrom-Json).event}
            }
        }
    )
    $lifecycle=@($events | Where-Object {$_ -cin @('profile.started','profile.failed')})
    $activeEvents=@(Get-Content -LiteralPath $path | ForEach-Object {($_ | ConvertFrom-Json).event})
    if (($lifecycle -join ',') -cne 'profile.started,profile.failed' -or
        $activeEvents[-1] -cne 'profile.failed') {
        throw 'Profile stream did not persist ordered sanitized failure lifecycle events.'
    }
    Write-Output 'Collector logging: schema allowlist, all three Setup events in collector stream, no customer values, silent stdout, bounded rotation, invalid/missing/UNC/ADS/reparse paths, locked-file fail-closed and offline profile stderr/stdout passed.'
}
finally {
    if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    Remove-Item -LiteralPath $directory -Recurse -Force
}
