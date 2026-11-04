Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')

function Get-ScomSetupSdkFileIdentity {
    param([string]$Path)
    [Reflection.AssemblyName]::GetAssemblyName($Path)
}

function Get-ScomSetupLocalScom {
    param([string]$ModulePath)
    $paths=@()
    if ($ModulePath) { $paths=@($ModulePath) }
    else {
        $paths=@(Get-Module -ListAvailable OperationsManager | Select-Object -First 10 -ExpandProperty Path)
        $paths+=@(
            (Join-Path $env:ProgramFiles 'Microsoft System Center\Operations Manager\Powershell\OperationsManager\OperationsManager.psd1'),
            (Join-Path $env:ProgramFiles 'Microsoft System Center\Operations Manager\Console\WindowsPowerShell\OperationsManager\OperationsManager.psd1'))
    }
    $selected='';$manifestValid=$false;$sdkValid=$false;$failureCode=$null
    foreach ($candidate in $paths | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        Assert-ScomSetupPath $candidate
        $selected=$candidate
        try {
            # Parse data only. Do not import SCOM code or establish a connection during preflight.
            $manifest=Import-PowerShellDataFile -LiteralPath $candidate
            $manifestValid=($null -ne $manifest['ModuleVersion'] -and [version]$manifest['ModuleVersion'] -ge [version]'1.0' -and
                ($manifest['RootModule'] -or $manifest['ModuleToProcess'] -or $manifest['NestedModules']))
            $moduleDirectory=Split-Path $candidate -Parent
            $rootModule=if ($manifest['RootModule']) { $manifest['RootModule'] } else { $manifest['ModuleToProcess'] }
            if ($rootModule -and -not (Test-Path -LiteralPath (Join-Path $moduleDirectory $rootModule) -PathType Leaf)) {
                $manifestValid=$false
            }
            $assemblyDirectories=@($moduleDirectory,
                (Join-Path $env:ProgramFiles 'Microsoft System Center\Operations Manager\Console\SDK Binaries'),
                (Join-Path $env:ProgramFiles 'Microsoft System Center\Operations Manager\Server\SDK Binaries'))
            $ancestor=$moduleDirectory
            for ($depth=0;$depth -lt 3;$depth++) {
                $ancestor=Split-Path $ancestor -Parent
                if (-not $ancestor) { break }
                foreach ($relative in @('SDK Binaries','Console\SDK Binaries','Server\SDK Binaries')) {
                    $assemblyDirectories+=Join-Path $ancestor $relative
                }
            }
            foreach ($required in @($manifest['RequiredAssemblies'])) {
                if ($required -is [string] -and $required.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)) {
                    $assemblyDirectories+=Split-Path ([IO.Path]::GetFullPath((Join-Path $moduleDirectory $required))) -Parent
                }
            }
            foreach ($assemblyDirectory in $assemblyDirectories) {
                $valid=$true
                foreach ($name in @('Microsoft.EnterpriseManagement.Core','Microsoft.EnterpriseManagement.OperationsManager','Microsoft.EnterpriseManagement.Runtime')) {
                    $file=Join-Path $assemblyDirectory ($name+'.dll')
                    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $valid=$false; break }
                    $identity=Get-ScomSetupSdkFileIdentity $file
                    if ($identity.Name -cne $name -or $identity.Version.Major -lt 7 -or
                        ([BitConverter]::ToString($identity.GetPublicKeyToken())).Replace('-','').ToLowerInvariant() -cne '31bf3856ad364e35') {
                        $valid=$false;break
                    }
                }
                if ($valid) { $sdkValid=$true;break }
            }
        } catch {
            $manifestValid=$false;$sdkValid=$false
            $failureCode='SETUP_LOCAL_SCOM_METADATA_PROBE_FAILED'
        }
        break
    }
    [pscustomobject]@{modulePath=$selected;moduleValid=$manifestValid;sdkValid=$sdkValid;failureCode=$failureCode}
}

function Get-ScomSetupLocalCatalog {
    $scom=Get-ScomSetupLocalScom ''
    $sites=@();$pools=@();$certificates=@()
    if (Get-Module -ListAvailable WebAdministration) {
        Import-Module WebAdministration -ErrorAction Stop | Out-Null
        $sites=@(Get-Website | Select-Object -First 100 | ForEach-Object {
            $site=$_
            [pscustomobject]@{name=[string]$site.Name
                applications=@(Get-WebApplication -Site $site.Name | Select-Object -First 200 | ForEach-Object { [string]$_.Path })
                httpsBindings=@(Get-WebBinding -Name $site.Name -Protocol https | Select-Object -First 100 | ForEach-Object {
                    $parts=([string]$_.bindingInformation) -split ':'
                    $port=0
                    if (-not [int]::TryParse($parts[-2],[ref]$port) -or $port -lt 1 -or $port -gt 65535) {
                        throw 'SETUP_LOCAL_IIS_BINDING_PROBE_FAILED'
                    }
                    [pscustomobject]@{host=$parts[-1];port=$port}
                })}
        })
        $pools=@(Get-ChildItem IIS:\AppPools | Select-Object -First 200 | ForEach-Object { [string]$_.Name })
    }
    $now=Get-Date
    $certificates=@(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now -and
        @($_.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.1' }).Count -gt 0
    } | Select-Object -First 100 | ForEach-Object {
        [pscustomobject]@{thumbprint=$_.Thumbprint;displayName=([string]$_.Subject+'; expires '+$_.NotAfter.ToString('yyyy-MM-dd'))
            dnsNames=@($_.DnsNameList | ForEach-Object { [string]$_.Unicode })}
    })
    [pscustomobject]@{sites=$sites;pools=$pools;certificates=$certificates;operationsManagerModule=$scom.modulePath
        currentAccount=[Security.Principal.WindowsIdentity]::GetCurrent().Name}
}

function Get-ScomSetupPrerequisites {
    param($Plan)
    $codes = [Collections.Generic.List[string]]::new()
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or
        $PSVersionTable.PSVersion -lt [version]'5.1') { $codes.Add('WINDOWS_POWERSHELL_51_REQUIRED') }
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        if ($os.ProductType -eq 1 -or [int]$os.BuildNumber -notin @(17763,20348,26100)) { $codes.Add('REVIEW_SUPPORTED_WINDOWS_SERVER_REQUIRED') }
    } catch { $codes.Add('OS_PROBE_FAILED'); $codes.Add('REVIEW_SUPPORTED_WINDOWS_SERVER_REQUIRED') }
    try {
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue
    if (-not $dotnet) { $codes.Add('DOTNET8_RUNTIME_REQUIRED') }
    else {
        $runtimes = & $dotnet.Source --list-runtimes
        if ($LASTEXITCODE -ne 0) { $codes.Add('DOTNET8_RUNTIME_PROBE_FAILED') }
        foreach ($runtime in @('Microsoft.NETCore.App','Microsoft.AspNetCore.App')) {
            if (-not ($runtimes -match "^$([regex]::Escape($runtime)) 8\.")) { $codes.Add("$runtime`_8_REQUIRED") }
        }
    }
    } catch { $codes.Add('DOTNET8_RUNTIME_PROBE_FAILED') }
    try {
        if (-not (Get-Module -ListAvailable WebAdministration)) { $codes.Add('IIS_MANAGEMENT_TOOLS_REQUIRED') }
        $iisServices = @(Get-Service -Name W3SVC,WAS -ErrorAction SilentlyContinue)
        if ($iisServices.Count -ne 2) { $codes.Add('IIS_WEB_SERVER_REQUIRED') }
    } catch { $codes.Add('IIS_PROBE_FAILED');$codes.Add('IIS_MANAGEMENT_TOOLS_REQUIRED');$codes.Add('IIS_WEB_SERVER_REQUIRED') }
    try {
        $bundles = @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
            Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match '^Microsoft \.NET 8\.0\..*Windows Server Hosting' })
        if ($bundles.Count -eq 0 -or -not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
            $codes.Add('DOTNET8_HOSTING_BUNDLE_REQUIRED')
        }
    } catch { $codes.Add('HOSTING_BUNDLE_PROBE_FAILED');$codes.Add('DOTNET8_HOSTING_BUNDLE_REQUIRED') }
    if (-not [Environment]::Is64BitProcess) { $codes.Add('X64_POWERSHELL_AND_HOSTING_REQUIRED') }
    try { $localScom=Get-ScomSetupLocalScom $Plan.operationsManagerModule }
    catch { $localScom=[pscustomobject]@{failureCode='SETUP_LOCAL_SCOM_METADATA_PROBE_FAILED';moduleValid=$false;sdkValid=$false} }
    if ($localScom.failureCode) { $codes.Add($localScom.failureCode) }
    if (-not $localScom.moduleValid) { $codes.Add('APPROVED_SCOM_CONSOLE_SDK_REQUIRED') }
    if (-not $localScom.sdkValid) { $codes.Add('SCOM_SDK_ASSEMBLY_METADATA_REQUIRED') }
    try {
    foreach ($key in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')) {
        if (Test-Path $key) { $codes.Add('RESTART_REQUIRED') }
    }
    $sessionManager = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    if ($sessionManager.PSObject.Properties['PendingFileRenameOperations'] -and $sessionManager.PendingFileRenameOperations) {
        $codes.Add('RESTART_REQUIRED')
    }
    } catch { $codes.Add('RESTART_PROBE_FAILED');$codes.Add('RESTART_REQUIRED') }
    $checks = @(
        [pscustomobject]@{name='Windows PowerShell 5.1'; passed=('WINDOWS_POWERSHELL_51_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='Supported Windows Server family'; passed=('REVIEW_SUPPORTED_WINDOWS_SERVER_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='.NET and ASP.NET Core 8 runtimes'; passed=(@($codes | Where-Object { $_ -match 'DOTNET8_RUNTIME|_8_REQUIRED' }).Count -eq 0)}
        [pscustomobject]@{name='IIS management tools'; passed=('IIS_MANAGEMENT_TOOLS_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='IIS web server and activation services installed'; passed=('IIS_WEB_SERVER_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='.NET 8 Hosting Bundle and ANCM'; passed=('DOTNET8_HOSTING_BUNDLE_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='64-bit execution'; passed=('X64_POWERSHELL_AND_HOSTING_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='Local OperationsManager module manifest'; passed=('APPROVED_SCOM_CONSOLE_SDK_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='Local SCOM SDK assembly identity metadata'; passed=('SCOM_SDK_ASSEMBLY_METADATA_REQUIRED' -notin $codes)}
        [pscustomobject]@{name='No detected pending reboot'; passed=('RESTART_REQUIRED' -notin $codes)}
    )
    $remedies=@{
        'Windows PowerShell 5.1'='Use the installed x64 Windows PowerShell 5.1; resolve customer script signing policy separately.'
        'Supported Windows Server family'='Use supported Windows Server 2019, 2022 or 2025; GUI requires Desktop Experience.'
        '.NET and ASP.NET Core 8 runtimes'='Install the approved .NET 8 Hosting Bundle. The GUI bundled runtime does not install server runtimes.'
        'IIS management tools'='Install approved IIS management tools through the separately confirmed prerequisite action.'
        'IIS web server and activation services installed'='Install approved IIS web-server/WAS components through the separately confirmed prerequisite action.'
        '.NET 8 Hosting Bundle and ANCM'='Provide the Microsoft-signed approved Hosting Bundle installer/version/SHA-256; complete customer IIS maintenance.'
        '64-bit execution'='Run the x64 setup package and x64 Windows PowerShell.'
        'Local OperationsManager module manifest'='Install the approved SCOM Operations Console/PowerShell component or select its installed module in Advanced. This checks local data only, not source connectivity.'
        'Local SCOM SDK assembly identity metadata'='Install matching supported SCOM SDK binaries. Names, declared Microsoft key token and version are checked locally; this is not signature verification or actual SDK/query compatibility, which remains a separate approved source gate.'
        'No detected pending reboot'='Complete the required customer-approved reboot, then rerun checks. Setup never reboots automatically.'
    }
    foreach ($check in $checks) { $check | Add-Member NoteProperty remedy $remedies[$check.name] }
    $probeFailures=@($codes | Where-Object { $_ -match 'PROBE_FAILED$' })
    if ($probeFailures.Count -gt 0) {
        $checks+=@([pscustomobject]@{name='Local prerequisite probe completion';passed=$false
            remedy=('Unable to inspect one or more local prerequisites ('+($probeFailures -join ', ')+'). Unknown is not missing or passed. Review local component access and approved administrator context; do not install based on an unreadable probe alone.')})
    }
    [pscustomobject]@{ ready=($codes.Count -eq 0); codes=$codes.ToArray(); checks=$checks; certificationStatus='notCertified' }
}

function Get-ScomSetupGroupCatalog {
    param($Plan,[scriptblock]$Approve = ${function:Confirm-ScomSetupEffect},[switch]$DiscoverManagementGroup)
    Assert-ScomSetupPath $Plan.operationsManagerModule
    $managementGroupId = [guid]::Empty
    if ($Plan.managementServer -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$' -or
        ((-not $DiscoverManagementGroup -or $Plan.managementGroupId) -and
        (-not [guid]::TryParseExact($Plan.managementGroupId,'D',[ref]$managementGroupId) -or
        $managementGroupId -eq [guid]::Empty))) { throw 'SETUP_DISCOVERY_SOURCE_INVALID' }
    if (-not (& $Approve "Read-only group discovery from the selected management group using this console identity. Maximum 200 groups, 60 seconds; reads group IDs/display names only, never inventory/members. No worker evidence flags are set.")) {
        throw 'SETUP_CANCELLED_NO_ACTION'
    }
    $job = Start-Job -ArgumentList $Plan,(Join-Path $PSScriptRoot 'ScomSetup.psm1'),([bool]$DiscoverManagementGroup) -ScriptBlock {
        param($p,$setupModule,$includeIdentity)
        $ErrorActionPreference = 'Stop'
        Import-Module $setupModule -ErrorAction Stop
        Import-Module $p.operationsManagerModule -ErrorAction Stop | Out-Null
        New-SCOMManagementGroupConnection -ComputerName $p.managementServer -ErrorAction Stop | Out-Null
        $managementGroups = @(Get-SCOMManagementGroup -ErrorAction Stop)
        if ($managementGroups.Count -ne 1 -or
            ($p.managementGroupId -and [string]$managementGroups[0].Id -ne $p.managementGroupId) -or
            (-not $includeIdentity -and -not $p.managementGroupId)) {
            throw 'SETUP_MANAGEMENT_GROUP_MISMATCH'
        }
        $classes = @(Get-SCOMClass -Name 'Microsoft.SystemCenter.InstanceGroup' -ErrorAction Stop)
        if ($classes.Count -ne 1 -or $classes[0].Name -cne 'Microsoft.SystemCenter.InstanceGroup') {
            throw 'SETUP_DISCOVERY_CLASS_INVALID'
        }
        $optionsType = 'Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
        $objectType = 'Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
        $classType = 'Microsoft.EnterpriseManagement.Configuration.ManagementPackClass' -as [type]
        if (-not $optionsType -or -not $objectType -or -not $classType) { throw 'SETUP_DISCOVERY_SDK_CONTRACT_UNAVAILABLE' }
        $manager = $managementGroups[0].EntityObjects
        $methods = @($manager.GetType().GetMethods() | Where-Object {
            $parameters = $_.GetParameters()
            $_.Name -eq 'GetObjectReader' -and $_.IsGenericMethodDefinition -and
                $parameters.Count -eq 2 -and $parameters[0].ParameterType -eq $classType -and
                $parameters[1].ParameterType -eq $optionsType
        })
        if ($methods.Count -ne 1) { throw 'SETUP_DISCOVERY_SDK_CONTRACT_UNAVAILABLE' }
        $options = [Activator]::CreateInstance($optionsType)
        $options.MaxResultCount = 201
        if ($options.MaxResultCount -ne 201) { throw 'SETUP_DISCOVERY_SDK_LIMIT_NOT_HONORED' }
        $reader = $methods[0].MakeGenericMethod($objectType).Invoke($manager, @($classes[0].PSObject.BaseObject,$options))
        try {
            $rows=@(Read-ScomSetupGroupRows $reader $classes[0])
            if ($includeIdentity) { [pscustomobject]@{managementGroupId=[string]$managementGroups[0].Id;groups=$rows} }
            else { $rows }
        }
        finally { if ($reader -is [IDisposable]) { $reader.Dispose() } }
    }
    try {
        if (-not (Wait-Job $job -Timeout 60)) { Stop-Job $job; throw 'SETUP_DISCOVERY_TIMEOUT' }
        if ($job.State -ne 'Completed') { throw 'SETUP_DISCOVERY_FAILED' }
        Receive-Job $job -ErrorAction Stop
    }
    finally { Remove-Job $job -Force }
}

function Set-ScomSetupDirectoryAcl {
    param([string]$Path, [hashtable]$Grants)
    Assert-ScomSetupPath $Path
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $all = @{'S-1-5-18'='FullControl'; 'S-1-5-32-544'='FullControl'}
    foreach ($name in $Grants.Keys) { $all[$name] = $Grants[$name] }
    foreach ($name in $all.Keys) {
        $principal = if ($name.StartsWith('S-1-')) { [Security.Principal.SecurityIdentifier]::new($name) }
            else { [Security.Principal.NTAccount]::new($name) }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($principal, $all[$name],
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    $actual = Get-Acl -LiteralPath $Path
    if (-not $actual.AreAccessRulesProtected) { throw 'SETUP_ACL_VERIFICATION_FAILED' }
    $expectedRules = @($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) |
        ForEach-Object { "$($_.IdentityReference.Value)|$($_.FileSystemRights)|$($_.InheritanceFlags)|$($_.PropagationFlags)|$($_.AccessControlType)" } | Sort-Object)
    $actualRules = @($actual.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) |
        ForEach-Object { "$($_.IdentityReference.Value)|$($_.FileSystemRights)|$($_.InheritanceFlags)|$($_.PropagationFlags)|$($_.AccessControlType)" } | Sort-Object)
    if (@(Compare-Object $expectedRules $actualRules).Count -ne 0 -or
        $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544') { throw 'SETUP_ACL_VERIFICATION_FAILED' }
}

function Assert-ScomSetupPrivateDirectory {
    param([string]$Path)
    Assert-ScomSetupPath $Path
    $acl = Get-Acl -LiteralPath $Path
    $ownerSid = ([Security.Principal.NTAccount]::new($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value
    if (-not $acl.AreAccessRulesProtected -or $ownerSid -notin @('S-1-5-32-544','S-1-5-18')) {
        throw 'SETUP_JOURNAL_REQUIRES_ADMIN_OWNED_PRIVATE_DIRECTORY'
    }
    foreach ($rule in $acl.Access) {
        $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($rule.AccessControlType -eq 'Allow' -and $sid -notin @('S-1-5-18','S-1-5-32-544')) {
            throw 'SETUP_JOURNAL_MUST_ONLY_ALLOW_SYSTEM_AND_ADMINISTRATORS'
        }
    }
}

function Assert-ScomSetupIisTarget {
    param($Plan)
    Import-Module WebAdministration
    if (Test-Path "IIS:\AppPools\$($Plan.poolName)") { throw 'SETUP_EXISTING_POOL_CONFLICT' }
    $site = Get-Website -Name $Plan.siteName
    if ($Plan.applicationPath -eq '/') {
        if ($site) { throw 'SETUP_EXISTING_SITE_CONFLICT' }
        $uri = [uri]$Plan.httpsBaseUrl
        if (@(Get-WebBinding | Where-Object {
            $_.bindingInformation -eq "*:$($uri.Port):$($uri.DnsSafeHost)"
        }).Count -gt 0) { throw 'SETUP_EXISTING_HTTPS_BINDING_CONFLICT' }
    } else {
        if (-not $site -or (Get-WebApplication -Site $Plan.siteName -Name $Plan.applicationPath.Trim('/'))) {
            throw 'SETUP_EXISTING_APPLICATION_CONFLICT_OR_SITE_MISSING'
        }
        $uri = [uri]$Plan.httpsBaseUrl
        $binding = @(Get-WebBinding -Name $Plan.siteName -Protocol https | Where-Object {
            $_.bindingInformation -eq "*:$($uri.Port):$($uri.DnsSafeHost)"
        })
        if ($binding.Count -ne 1 -or $binding[0].certificateHash -ne $Plan.tlsThumbprint) { throw 'SETUP_EXISTING_SITE_HTTPS_MISMATCH' }
    }
    $certificate = Get-Item "Cert:\LocalMachine\My\$($Plan.tlsThumbprint)"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
        throw 'SETUP_TLS_CERTIFICATE_INVALID'
    }
}

function Assert-ScomSetupOwnedPool {
    param($Plan)
    Import-Module WebAdministration
    $pool = Get-Item "IIS:\AppPools\$($Plan.poolName)"
    if ($pool.processModel.identityType -ne 'ApplicationPoolIdentity' -or $pool.managedRuntimeVersion -ne '' -or
        $pool.managedPipelineMode -ne 'Integrated') { throw 'SETUP_POOL_OWNERSHIP_CONFLICT' }
    $path = if ($Plan.applicationPath -eq '/') { (Get-Website -Name $Plan.siteName).physicalPath }
        else { (Get-WebApplication -Site $Plan.siteName -Name $Plan.applicationPath.Trim('/')).physicalPath }
    $assigned = if ($Plan.applicationPath -eq '/') { (Get-Website -Name $Plan.siteName).applicationPool }
        else { (Get-WebApplication -Site $Plan.siteName -Name $Plan.applicationPath.Trim('/')).applicationPool }
    if ($path -ne (Join-Path $Plan.installPath 'server') -or $assigned -ne $Plan.poolName) { throw 'SETUP_APP_OWNERSHIP_CONFLICT' }
    $uses = @(Get-Website | Where-Object applicationPool -EQ $Plan.poolName)
    $apps = @(Get-WebApplication | Where-Object applicationPool -EQ $Plan.poolName)
    if ($uses.Count + $apps.Count -ne 1) { throw 'SETUP_POOL_NOT_DEDICATED' }
    $variables = Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/applicationPools/add[@name='$($Plan.poolName)']/environmentVariables" -Name '.'
    if ($variables.Collection.Count -gt 0) {
        throw 'SETUP_POOL_ENVIRONMENT_OVERRIDES_REQUIRE_MANUAL_REVIEW'
    }
}

function Assert-ScomSetupNewTargets {
    param($Plan)
    Assert-ScomSetupPlan $Plan
    Assert-ScomSetupIisTarget $Plan
    $null = [Security.Principal.NTAccount]::new($Plan.workerAccount).Translate([Security.Principal.SecurityIdentifier])
    foreach ($path in @($Plan.installPath,$Plan.privatePath,$Plan.receiverStatePath,$Plan.workerStatePath,$Plan.mcpLogPath,$Plan.collectorLogPath)) {
        if (Test-Path -LiteralPath $path) { throw 'SETUP_NEW_DIRECTORIES_REQUIRED' }
        if (-not (Test-Path -LiteralPath (Split-Path $path -Parent) -PathType Container)) { throw 'SETUP_EXISTING_PARENT_DIRECTORY_REQUIRED' }
    }
    foreach ($file in @('server\ScomMcp.dll','server\web.config','server\appsettings.ReadModel.example.json',
        'server\appsettings.Worker.example.json','collector\Invoke-SCOMGenericGroupProfile.ps1','collector\ScomOperationalLogging.psm1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Plan.packagePath $file) -PathType Leaf)) { throw 'SETUP_PACKAGE_INCOMPLETE' }
    }
}

function New-ScomSetupWindowsAdapters {
    @{
        Prerequisites = ${function:Get-ScomSetupPrerequisites}
        Log = {
            param($p,$eventName)
            Assert-ScomSetupInstalledFiles $p
            Import-Module (Join-Path $p.installPath 'collector\ScomOperationalLogging.psm1') -ErrorAction Stop
            $log = New-SCOMOperationalLog -Configuration ([pscustomobject]@{
                directory=$p.collectorLogPath; maxFileBytes=1048576; retainedFiles=5
            })
            Write-SCOMOperationalEvent -Log $log -Event $eventName
        }
        Prepare = {
            param($p)
            Assert-ScomSetupNewTargets $p
            foreach ($path in @($p.installPath,$p.privatePath,$p.receiverStatePath,$p.workerStatePath,$p.mcpLogPath,$p.collectorLogPath)) {
                $null = New-Item -ItemType Directory -Path $path
                Set-ScomSetupDirectoryAcl $path @{}
            }
            # The pool SID cannot be resolved until activation creates the pool.
            Set-ScomSetupDirectoryAcl $p.installPath @{$p.workerAccount='ReadAndExecute'}
            Set-ScomSetupDirectoryAcl $p.privatePath @{$p.workerAccount='ReadAndExecute'}
            Set-ScomSetupDirectoryAcl $p.workerStatePath @{$p.workerAccount='Modify'}
            Set-ScomSetupDirectoryAcl $p.collectorLogPath @{$p.workerAccount='Modify'}
            foreach ($directory in @('server','collector')) {
                foreach ($file in Get-ChildItem -LiteralPath (Join-Path $p.packagePath $directory) -Recurse -Force) {
                    Assert-ScomSetupPath $file.FullName
                }
                Copy-Item -LiteralPath (Join-Path $p.packagePath $directory) -Destination $p.installPath -Recurse
            }
            # Packaged synthetic auth is not a deployable configuration.
            [IO.File]::WriteAllText((Join-Path $p.installPath 'server\appsettings.json'), '{}', [Text.UTF8Encoding]::new($false))
            Save-ScomSetupJson (Join-Path $p.privatePath 'setup-plan.json') $p
            $files = @(Get-ChildItem -LiteralPath $p.installPath -Recurse -File | Where-Object Name -NE 'appsettings.json' |
                ForEach-Object { [pscustomobject]@{
                    path=$_.FullName.Substring($p.installPath.TrimEnd('\').Length + 1)
                    sha256=(Get-ScomSetupHash $_.FullName)
                } })
            Save-ScomSetupJson (Join-Path $p.privatePath 'installed-files.json') @{schemaVersion=1; files=$files}
        }
        Configure = {
            param($p,$inputSettings)
            Assert-ScomSetupInstalledFiles $p
            foreach ($path in @($inputSettings.identityPath,$inputSettings.receiverPath,$inputSettings.workerPath)) {
                Assert-ScomSetupPath $path
                & dotnet (Join-Path $p.installPath 'server\ScomMcp.dll') --validate-setup-json $path
                if ($LASTEXITCODE -ne 0) { throw 'SETUP_INPUT_JSON_INVALID_OR_DUPLICATE' }
            }
            $identity = Read-ScomSetupJson $inputSettings.identityPath
            $receiver = Read-ScomSetupJson $inputSettings.receiverPath
            $worker = Read-ScomSetupJson $inputSettings.workerPath
            $merged = Merge-ScomSetupConfiguration $p $receiver $worker $identity
            $active = Join-Path $p.installPath 'server\appsettings.json'
            if (Test-Path -LiteralPath (Join-Path $p.installPath 'server\appsettings.Production.json')) {
                throw 'SETUP_EXTRA_CONFIGURATION_PROVIDER_REQUIRES_MANUAL_REVIEW'
            }
            $backup = Join-Path $p.privatePath ('receiver.before-setup.' + [guid]::NewGuid().ToString('N') + '.json')
            Copy-Item -LiteralPath $active -Destination $backup
            $workerPath = Join-Path $p.privatePath 'worker.json'
            if (Test-Path -LiteralPath $workerPath) {
                Copy-Item -LiteralPath $workerPath -Destination (Join-Path $p.privatePath ('worker.before-setup.' + [guid]::NewGuid().ToString('N') + '.json'))
            }
            Save-ScomSetupJson $active $merged.receiver
            Save-ScomSetupJson $workerPath $merged.worker
        }
        Validate = {
            param($p)
            Assert-ScomSetupInstalledFiles $p
            & dotnet (Join-Path $p.installPath 'server\ScomMcp.dll') --validate-setup `
                (Join-Path $p.installPath 'server') (Join-Path $p.privatePath 'worker.json')
            if ($LASTEXITCODE -eq 2) { throw 'SETUP_SOURCE_EVIDENCE_REQUIRED_NO_FLAGS_INFERRED' }
            if ($LASTEXITCODE -ne 0) { throw 'SETUP_EFFECTIVE_CONFIGURATION_INVALID' }
        }
        Activate = {
            param($p)
            Assert-ScomSetupIisTarget $p
            Add-Type -Path "$env:WINDIR\System32\inetsrv\Microsoft.Web.Administration.dll"
            $manager = [Microsoft.Web.Administration.ServerManager]::new()
            try {
                if ($manager.ApplicationPools[$p.poolName]) { throw 'SETUP_EXISTING_POOL_CONFLICT' }
                $pool = $manager.ApplicationPools.Add($p.poolName)
                $pool.ManagedRuntimeVersion = ''
                $pool.ManagedPipelineMode = [Microsoft.Web.Administration.ManagedPipelineMode]::Integrated
                $pool.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity
                $pool.AutoStart = $false
                if ($p.applicationPath -eq '/') {
                    if ($manager.Sites[$p.siteName]) { throw 'SETUP_EXISTING_SITE_CONFLICT' }
                    $uri = [uri]$p.httpsBaseUrl
                    $site = $manager.Sites.Add($p.siteName, 'https', "*:$($uri.Port):$($uri.DnsSafeHost)", (Join-Path $p.installPath 'server'))
                    $site.ServerAutoStart = $false
                    $site.Applications['/'].ApplicationPoolName = $p.poolName
                    $site.Bindings[0].CertificateHash = (Get-Item "Cert:\LocalMachine\My\$($p.tlsThumbprint)").GetCertHash()
                    $site.Bindings[0].CertificateStoreName = 'My'
                    $site.Bindings[0].SslFlags = [Microsoft.Web.Administration.SslFlags]::Sni
                } else {
                    $site = $manager.Sites[$p.siteName]
                    if (-not $site -or $site.Applications[$p.applicationPath]) { throw 'SETUP_EXISTING_APPLICATION_CONFLICT_OR_SITE_MISSING' }
                    $application = $site.Applications.Add($p.applicationPath, (Join-Path $p.installPath 'server'))
                    $application.ApplicationPoolName = $p.poolName
                }
                # Commit pool and TLS-bound site/app together, both stopped.
                $manager.CommitChanges()
            }
            finally { $manager.Dispose() }
            $poolAccount = "IIS APPPOOL\$($p.poolName)"
            Set-ScomSetupDirectoryAcl $p.installPath @{$p.workerAccount='ReadAndExecute'; $poolAccount='ReadAndExecute'}
            Set-ScomSetupDirectoryAcl $p.receiverStatePath @{$poolAccount='Modify'}
            Set-ScomSetupDirectoryAcl $p.mcpLogPath @{$poolAccount='Modify'}
            if ($p.applicationPath -eq '/') {
                Start-Website -Name $p.siteName
            }
            Assert-ScomSetupOwnedPool $p
            Start-WebAppPool -Name $p.poolName
        }
        Publish = {
            param($p)
            Assert-ScomSetupOwnedPool $p
            $receipt = Read-ScomSetupJson (Join-Path $p.workerStatePath 'setup-publication.json')
            if ($receipt.schemaVersion -ne 1 -or $receipt.event -cne 'SCOM_WORKER_PUBLISHED' -or
                $receipt.workerAccount -ne $p.workerAccount -or
                $receipt.workerHash -ne (Get-ScomSetupHash (Join-Path $p.privatePath 'worker.json')) -or
                $receipt.receiverHash -ne (Get-ScomSetupHash (Join-Path $p.installPath 'server\appsettings.json'))) {
                throw 'SETUP_PUBLICATION_RECEIPT_MISMATCH'
            }
        }
        Accept = { param($p) Assert-ScomSetupOwnedPool $p }
        EnableStartup = {
            param($p)
            Assert-ScomSetupOwnedPool $p
            $manager = [Microsoft.Web.Administration.ServerManager]::new()
            try {
                $manager.ApplicationPools[$p.poolName].AutoStart = $true
                if ($p.applicationPath -eq '/') { $manager.Sites[$p.siteName].ServerAutoStart = $true }
                $manager.CommitChanges()
            }
            finally { $manager.Dispose() }
        }
        Health = {
            param($p)
            $response = Invoke-WebRequest -UseBasicParsing -Uri ($p.httpsBaseUrl.TrimEnd('/') + '/healthz') -TimeoutSec 30 -MaximumRedirection 0
            $health = $response.Content | ConvertFrom-Json -ErrorAction Stop
            if ($response.StatusCode -ne 200 -or $health.service -cne 'ScomReadOnlyMcp' -or $health.version -cne '1.0.0') {
                throw 'SETUP_HTTPS_HEALTH_FAILED_NOT_READER_ACCEPTANCE'
            }
        }
        Stop = {
            param($p)
            Assert-ScomSetupOwnedPool $p
            Set-ItemProperty "IIS:\AppPools\$($p.poolName)" -Name autoStart -Value $false
            Stop-WebAppPool -Name $p.poolName
        }
    }
}

Export-ModuleMember -Function Get-ScomSetupPrerequisites, Get-ScomSetupLocalCatalog, Get-ScomSetupGroupCatalog, Set-ScomSetupDirectoryAcl, Assert-ScomSetupPrivateDirectory, Assert-ScomSetupNewTargets, New-ScomSetupWindowsAdapters
