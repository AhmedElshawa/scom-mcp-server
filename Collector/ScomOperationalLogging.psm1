Set-StrictMode -Version Latest

function Assert-SCOMLogDirectory {
    param([Parameter(Mandatory)] [string]$Directory)
    if ($Directory -notmatch '^[A-Za-z]:\\' -or $Directory -match '[\x00-\x1f*?"<>|]' -or
        $Directory.Substring(2).Contains(':') -or $Directory -match '(^|\\)\.\.?($|\\)' -or
        $Directory -match '[ .](\\|$)') { throw 'SCOM_LOG_PATH_INVALID' }
    $full=[IO.Path]::GetFullPath($Directory)
    $drive=New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.DriveFormat -cne 'NTFS') {
        throw 'SCOM_LOG_VOLUME_INVALID'
    }
    $item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) { throw 'SCOM_LOG_DIRECTORY_REQUIRED' }
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SCOM_LOG_REPARSE_REFUSED' }
        $item=$item.Parent
    }
    return $full
}

function New-SCOMOperationalLog {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [object]$Configuration)
    $max=1048576; $retained=5
    if ($null -ne $Configuration.PSObject.Properties['maxFileBytes']) { $max=$Configuration.maxFileBytes }
    if ($null -ne $Configuration.PSObject.Properties['retainedFiles']) { $retained=$Configuration.retainedFiles }
    if ($max -isnot [int] -and $max -isnot [long]) { throw 'SCOM_LOG_BUDGET_INVALID' }
    if ($retained -isnot [int] -and $retained -isnot [long]) { throw 'SCOM_LOG_BUDGET_INVALID' }
    if ($max -lt 1024 -or $max -gt 16777216 -or $retained -lt 1 -or $retained -gt 20) {
        throw 'SCOM_LOG_BUDGET_INVALID'
    }
    $directory=Assert-SCOMLogDirectory ([string]$Configuration.directory)
    [pscustomobject]@{Directory=$directory;MaxFileBytes=[int]$max;RetainedFiles=[int]$retained}
}

function Write-SCOMOperationalEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [object]$Log,
        [Parameter(Mandatory)]
        [ValidateSet('profile.started','configuration.validated','source.connected','scan.completed','profile.failed',
            'setup.started','setup.completed','setup.failed')]
        [string]$Event
    )
    $directory=Assert-SCOMLogDirectory $Log.Directory
    $path=Join-Path $directory 'scom-collector-profile.jsonl'
    # RetainedFiles includes the active file. Only fixed, collector-owned names are touched.
    for ($i=0; $i -lt $Log.RetainedFiles; $i++) {
        $candidate=$path
        if ($i -gt 0) { $candidate="$path.$i" }
        if (Test-Path -LiteralPath $candidate) {
            $item=Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
            if ($item.PSIsContainer -or $item.Length -gt $Log.MaxFileBytes -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'SCOM_LOG_FILE_INVALID'
            }
        }
    }
    $record=[ordered]@{
        schemaVersion=1; timestampUtc=[DateTimeOffset]::UtcNow.ToString('o')
        component=$(if ($Event.StartsWith('setup.',[StringComparison]::Ordinal)) {'setup'} else {'collectorProfile'})
        event=$Event
    }
    $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes(($record | ConvertTo-Json -Compress)+"`n")
    $stream=$null
    try {
        $stream=[IO.File]::Open($path,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::Write,[IO.FileShare]::None)
        if ($stream.Length+$bytes.Length -gt $Log.MaxFileBytes) {
            $stream.Dispose(); $stream=$null
            if ($Log.RetainedFiles -eq 1) { [IO.File]::Delete($path) }
            else {
                $last=$Log.RetainedFiles-1
                [IO.File]::Delete("$path.$last")
                for ($i=$last-1; $i -ge 1; $i--) {
                    if ([IO.File]::Exists("$path.$i")) { [IO.File]::Move("$path.$i","$path.$($i+1)") }
                }
                [IO.File]::Move($path,"$path.1")
            }
            $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        }
        $null=$stream.Seek(0,[IO.SeekOrigin]::End)
        $stream.Write($bytes,0,$bytes.Length)
        $stream.Flush($true)
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

Export-ModuleMember -Function New-SCOMOperationalLog, Write-SCOMOperationalEvent
