Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')

function Assert-ScomHostingInstallerEvidence {
    param($Evidence, [string]$ExpectedVersion, [string]$ExpectedSha256)
    if ($ExpectedVersion -notmatch '^8\.0\.[0-9]+$' -or $ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        $Evidence.signatureStatus -ne 'Valid' -or
        $Evidence.signerSubject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)' -or
        $Evidence.sha256 -ine $ExpectedSha256 -or
        $Evidence.fileName -ine "dotnet-hosting-$ExpectedVersion-win.exe" -or
        $Evidence.productVersion -notmatch ('^' + [regex]::Escape($ExpectedVersion) + '($|[.+ -])')) {
        throw 'SETUP_HOSTING_INSTALLER_VENDOR_SIGNATURE_VERSION_OR_CHECKSUM_INVALID'
    }
}

function Invoke-ScomPrerequisiteInstall {
    param(
        [string]$JournalPath, [hashtable]$Adapters,
        [switch]$InstallIis, [string]$InstallerPath, [string]$ExpectedVersion, [string]$ExpectedSha256,
        [switch]$AcknowledgeRestart,
        [scriptblock]$Approve = ${function:Confirm-ScomSetupEffect}
    )
    if (-not $InstallIis -and -not $InstallerPath -and -not $AcknowledgeRestart) {
        throw 'SETUP_PREREQUISITE_INSTALL_REQUIRES_EXPLICIT_OPT_IN'
    }
    $facts = & $Adapters.Probe $ExpectedVersion
    if (-not $facts.supportedHost) { throw 'SETUP_PREREQUISITE_INSTALL_UNSUPPORTED_HOST' }
    $state = if (Test-Path -LiteralPath $JournalPath) { Read-ScomSetupJson $JournalPath }
        else { [pscustomobject]@{
            schemaVersion=1; pendingAction=$null; restartBootUtc=$null; completed=@(); processId=$null
        } }
    if ($state.schemaVersion -ne 1) { throw 'SETUP_PREREQUISITE_JOURNAL_VERSION' }
    if ($state.pendingAction) { throw 'SETUP_INTERRUPTED_PREREQUISITE_EFFECT_REVIEW_REQUIRED_NO_RETRY' }
    if ($state.restartBootUtc) {
        if (-not $AcknowledgeRestart -or $facts.restartPending -or
            ([DateTimeOffset]$facts.bootUtc).UtcDateTime -le ([DateTimeOffset]$state.restartBootUtc).UtcDateTime) {
            throw 'SETUP_PREREQUISITE_RESTART_REQUIRED_USE_CUSTOMER_MAINTENANCE_PROCESS'
        }
        if (-not (& $Approve 'Record independently detected newer OS boot and cleared pending restart. No reboot or IIS restart is performed.')) {
            throw 'SETUP_CANCELLED_NO_ACTION'
        }
        $state.restartBootUtc=$null
        Save-ScomSetupJson $JournalPath $state
    }
    if ($facts.restartPending) { throw 'SETUP_PREREQUISITE_EXISTING_RESTART_REQUIRED' }
    # Validate the whole installer evidence BEFORE any requested IIS mutation.
    if ($InstallerPath) {
        Assert-ScomSetupPath $InstallerPath
        $evidence = & $Adapters.Inspect $InstallerPath
        Assert-ScomHostingInstallerEvidence $evidence $ExpectedVersion $ExpectedSha256
    }
    function Invoke-Effect {
        param([string]$Action, [string]$Summary, [scriptblock]$Effect)
        if (-not (& $Approve $Summary)) { throw 'SETUP_CANCELLED_NO_ACTION' }
        $state.pendingAction=$Action
        Save-ScomSetupJson $JournalPath $state
        $result = & $Effect
        if ($result.PSObject.Properties['processId']) { $state.processId=$result.processId; Save-ScomSetupJson $JournalPath $state }
        if (-not $result.success) { throw 'SETUP_PREREQUISITE_EFFECT_FAILED_OR_TIMED_OUT_JOURNAL_RETAINED' }
        $state.completed += [pscustomobject]@{action=$Action; outcome=$result.outcome; utc=[DateTime]::UtcNow.ToString('o')}
        if ($result.restartRequired) { $state.restartBootUtc=$facts.bootUtc }
        $state.pendingAction=$null
        Save-ScomSetupJson $JournalPath $state
        if ($result.restartRequired) { throw 'SETUP_PREREQUISITE_RESTART_REQUIRED_USE_CUSTOMER_MAINTENANCE_PROCESS' }
    }
    if ($InstallIis -and -not $facts.iisReady) {
        Invoke-Effect 'InstallIis' 'Install ONLY missing local Web-Server, Web-Mgmt-Console and Web-Scripting-Tools plus required Windows dependencies. No all-subfeatures, remote host, credentials or automatic restart. Windows servicing follows customer source policy; this may affect the host and requires an approved maintenance window.' {
            & $Adapters.InstallIis
        }
        $facts = & $Adapters.Probe $ExpectedVersion
        if ($facts.restartPending) {
            $state.restartBootUtc=$facts.bootUtc
            Save-ScomSetupJson $JournalPath $state
            throw 'SETUP_PREREQUISITE_RESTART_REQUIRED_USE_CUSTOMER_MAINTENANCE_PROCESS'
        }
        if (-not $facts.iisReady) { throw 'SETUP_IIS_POSTINSTALL_VERIFICATION_FAILED' }
    }
    if ($InstallerPath) {
        if (-not $facts.iisReady) { throw 'SETUP_IIS_REQUIRED_BEFORE_HOSTING_BUNDLE' }
        $mode = if ($facts.exactBundleInstalled) { 'repair' } else { 'install' }
        Invoke-Effect 'HostingBundle' "Run approved Microsoft .NET Hosting Bundle $ExpectedVersion ($mode), SHA256 $ExpectedSha256, from $InstallerPath. Install runtime/shared framework/ANCM, preserve x86 support and shared-config safety checks. Private log; 30-minute wait, no forced termination or automatic reboot/IIS restart. This host-wide installer may affect existing applications; approved maintenance required." {
            # Recheck after approval and while the Windows adapter holds the
            # executable read-only. Inspect is repeated there before launch.
            Assert-ScomHostingInstallerEvidence (& $Adapters.Inspect $InstallerPath) $ExpectedVersion $ExpectedSha256
            & $Adapters.HostingBundle $InstallerPath $ExpectedVersion $ExpectedSha256 $mode
        }
        $facts = & $Adapters.Probe $ExpectedVersion
        if (-not $facts.exactBundleInstalled -or -not $facts.runtimeReady -or -not $facts.ancmReady) {
            throw 'SETUP_HOSTING_POSTINSTALL_VERIFICATION_FAILED'
        }
        # Even exit zero can require IIS to reload ANCM/PATH. Do not restart
        # shared host services here or imply the running app picked it up.
        Write-Output 'SETUP_HOSTING_INSTALLED_CUSTOMER_IIS_RESTART_REVIEW_REQUIRED. Reopen the shell and complete approved IIS/host maintenance before resuming setup.'
    }
    Write-Output 'SETUP_PREREQUISITE_EFFECTS_COMPLETE. SCOM media, script signing and normal setup prerequisite checks remain required.'
}

function New-ScomPrerequisiteWindowsAdapters {
    param([string]$StateDirectory)
    @{
        Probe = {
            param($version)
            $os=Get-CimInstance Win32_OperatingSystem
            Import-Module ServerManager -ErrorAction Stop
            $features=@(Get-WindowsFeature -Name Web-Server,Web-Mgmt-Console,Web-Scripting-Tools)
            $bundles=@(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
                Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.PSObject.Properties['DisplayVersion'] -and
                    $_.DisplayName -match '^Microsoft \.NET 8\.0\..*Windows Server Hosting' -and
                    $_.DisplayVersion -match ('^' + [regex]::Escape($version) + '($|\.)') })
            $restart=(Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') -or
                (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
            $session=Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
            if ($session.PSObject.Properties['PendingFileRenameOperations'] -and $session.PendingFileRenameOperations) { $restart=$true }
            $dotnet=Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
            $runtimeReady=$false
            if (Test-Path -LiteralPath $dotnet) {
                $runtimes=& $dotnet --list-runtimes
                if ($LASTEXITCODE -ne 0) { throw 'SETUP_RUNTIME_PROBE_FAILED' }
                $runtimeReady=[bool]($runtimes -match '^Microsoft.NETCore.App 8\.') -and [bool]($runtimes -match '^Microsoft.AspNetCore.App 8\.')
            }
            [pscustomobject]@{
                supportedHost=($PSVersionTable.PSEdition -eq 'Desktop' -and [Environment]::Is64BitProcess -and
                    $os.ProductType -ne 1 -and [int]$os.BuildNumber -in @(17763,20348,26100))
                bootUtc=$os.LastBootUpTime.ToUniversalTime().ToString('o'); restartPending=$restart
                iisReady=($features.Count -eq 3 -and @($features | Where-Object Installed -NE $true).Count -eq 0)
                exactBundleInstalled=($bundles.Count -eq 1); runtimeReady=$runtimeReady
                ancmReady=(Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")
            }
        }
        Inspect = {
            param($path)
            Assert-ScomSetupPath $path
            $file=Get-Item -LiteralPath $path
            if ($file.PSIsContainer -or $file.Length -gt 512MB -or $file.Length -lt 1MB) { throw 'SETUP_HOSTING_INSTALLER_SIZE_INVALID' }
            $signature=Get-AuthenticodeSignature -LiteralPath $path
            [pscustomobject]@{
                fileName=$file.Name; productVersion=$file.VersionInfo.ProductVersion
                sha256=(Get-ScomSetupHash $path); signatureStatus=[string]$signature.Status
                signerSubject=$(if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' })
            }
        }
        InstallIis = {
            $features=@(Get-WindowsFeature -Name Web-Server,Web-Mgmt-Console,Web-Scripting-Tools)
            if ($features.Count -ne 3) { throw 'SETUP_SUPPORTED_IIS_FEATURE_CONTRACT_REQUIRED' }
            $missing=@($features | Where-Object Installed -NE $true | ForEach-Object Name)
            if ($missing.Count -eq 0) { return [pscustomobject]@{success=$true;restartRequired=$false;outcome='AlreadyInstalled'} }
            $result=Install-WindowsFeature -Name $missing -IncludeManagementTools -ErrorAction Stop
            [pscustomobject]@{success=($result.Success -eq $true);restartRequired=([string]$result.RestartNeeded -eq 'Yes');outcome=[string]$result.ExitCode}
        }
        HostingBundle = {
            param($path,$version,$hash,$mode)
            $fileLease=[IO.File]::Open($path,'Open','Read','Read')
            $oldTemp=$env:TEMP; $oldTmp=$env:TMP
            try {
                $adapters=New-ScomPrerequisiteWindowsAdapters $StateDirectory
                Assert-ScomHostingInstallerEvidence (& $adapters.Inspect $path) $version $hash
                $log=Join-Path $StateDirectory ('hosting-' + [guid]::NewGuid().ToString('N') + '.log')
                $arguments=@("/$mode",'/quiet','/norestart','/log',"`"$log`"",
                    'OPT_NO_ANCM=0','OPT_NO_RUNTIME=0','OPT_NO_SHAREDFX=0','OPT_NO_X86=0','OPT_NO_SHARED_CONFIG_CHECK=0')
                # Only the installer child inherits these private temp paths;
                # restore process-local variables even on launch failure.
                $env:TEMP=$StateDirectory; $env:TMP=$StateDirectory
                $process=Start-Process -FilePath $path -ArgumentList $arguments -PassThru -ErrorAction Stop
                try {
                    if (-not $process.WaitForExit(1800000)) {
                        return [pscustomobject]@{success=$false;restartRequired=$false;outcome='TimeoutStillRunning';processId=$process.Id}
                    }
                    $code=$process.ExitCode
                    [pscustomobject]@{success=($code -in @(0,3010));restartRequired=($code -eq 3010);outcome="InstallerExit$code";processId=$process.Id}
                }
                finally { $process.Dispose() }
            }
            finally { $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $fileLease.Dispose() }
        }.GetNewClosure()
    }
}

Export-ModuleMember -Function Assert-ScomHostingInstallerEvidence, Invoke-ScomPrerequisiteInstall, New-ScomPrerequisiteWindowsAdapters
