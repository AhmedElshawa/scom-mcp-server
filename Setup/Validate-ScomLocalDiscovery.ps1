Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1') -Force
$module=Get-Module ScomSetup.Windows
$root=Join-Path (Split-Path $PSScriptRoot -Parent) ('.validation-data\local-discovery-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $root
$count=0
function Check([bool]$value) { if (-not $value) { throw 'Synthetic local metadata discovery assertion failed' };$script:count++ }
try {
    $manifest=Join-Path $root 'OperationsManager.psd1'
    [IO.File]::WriteAllText($manifest,"@{ModuleVersion='1.0';RootModule='OperationsManager.dll'}")
    [IO.File]::WriteAllText((Join-Path $root 'OperationsManager.dll'),'synthetic installed module fixture')
    $names=@('Microsoft.EnterpriseManagement.Core','Microsoft.EnterpriseManagement.OperationsManager','Microsoft.EnterpriseManagement.Runtime')
    foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $root ($name+'.dll')),'synthetic metadata fixture') }
    & $module {
        $script:identityValid=$true
        function script:Get-ScomSetupSdkFileIdentity {
            param($Path)
            $identity=[Reflection.AssemblyName]::new()
            $identity.Name=[IO.Path]::GetFileNameWithoutExtension($Path)
            $identity.Version=[version]'7.0.0.0'
            $token=if ($script:identityValid) { [byte[]]@(0x31,0xbf,0x38,0x56,0xad,0x36,0x4e,0x35) } else { [byte[]]@(1,2,3,4,5,6,7,8) }
            $identity.SetPublicKeyToken($token)
            $identity
        }
        function script:Import-Module { throw 'Automatic local probe must not import SCOM code' }
        function script:New-SCOMManagementGroupConnection { throw 'Automatic local probe must not contact SCOM' }
    }
    $r=& $module { param($p) Get-ScomSetupLocalScom $p } $manifest
    Check ($r.moduleValid -and $r.sdkValid -and $r.modulePath -ceq $manifest)
    & $module { $script:identityValid=$false }
    $r=& $module { param($p) Get-ScomSetupLocalScom $p } $manifest
    Check ($r.moduleValid -and -not $r.sdkValid)
    & $module { $script:identityValid=$true }
    Remove-Item -LiteralPath (Join-Path $root 'Microsoft.EnterpriseManagement.Runtime.dll')
    $r=& $module { param($p) Get-ScomSetupLocalScom $p } $manifest
    Check ($r.moduleValid -and -not $r.sdkValid)
    [IO.File]::WriteAllText($manifest,"@{ModuleVersion='1.0'}")
    $r=& $module { param($p) Get-ScomSetupLocalScom $p } $manifest
    Check (-not $r.moduleValid -and -not $r.sdkValid)
    [IO.File]::WriteAllText($manifest,"throw 'must not execute manifest text'")
    $r=& $module { param($p) Get-ScomSetupLocalScom $p } $manifest
    Check (-not $r.moduleValid -and -not $r.sdkValid -and $r.failureCode -ceq 'SETUP_LOCAL_SCOM_METADATA_PROBE_FAILED')
    & $module {
        function script:Get-ScomSetupLocalScom { [pscustomobject]@{modulePath='';moduleValid=$false;sdkValid=$false;failureCode=$null} }
        function script:Get-Module { param($ListAvailable) [pscustomobject]@{Name='WebAdministration'} }
        function script:Import-Module {}
        function script:Get-Website { [pscustomobject]@{Name='Existing'} }
        function script:Get-WebApplication { param($Site) @() }
        function script:Get-WebBinding { param($Name,$Protocol) [pscustomobject]@{bindingInformation='*:443:example.invalid'} }
        function script:Get-ChildItem {
            param($Path)
            if ($Path -ceq 'IIS:\AppPools') { [pscustomobject]@{Name='Shared'} }
            elseif ($Path -cne 'Cert:\LocalMachine\My') { throw 'Unexpected local catalog fixture probe' }
        }
    }
    $catalog=Get-ScomSetupLocalCatalog
    Check ($catalog.sites[0].httpsBindings[0].port -is [int] -and $catalog.sites[0].httpsBindings[0].port -eq 443)
    Check (($catalog | ConvertTo-Json -Depth 10 -Compress) -match '"port":443' -and $catalog.certificates.Count -eq 0)
    Write-Output "PASS: $count local SCOM manifest/SDK metadata assertions; fixtures only, no code import or connection. Metadata is not source compatibility."
} finally {
    Remove-Module ScomSetup.Windows
    Remove-Item -LiteralPath $root -Recurse -Force
}
