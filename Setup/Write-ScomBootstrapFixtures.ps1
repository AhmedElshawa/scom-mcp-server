[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Gui.psm1') -Force
$gui=Get-Module ScomSetup.Gui
$windows=& $gui { Get-Module ScomSetup.Windows }
$root=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Fixture output must be new.' }
$null=New-Item -ItemType Directory -Path $root
$fixture=[pscustomobject]@{count=0;blocked=$true}
& $windows {
    param($facts)
    $script:fixture=$facts
    function script:Get-ScomSetupLocalScom {
        param($ModulePath)
        [pscustomobject]@{modulePath='';moduleValid=$false;sdkValid=$false;failureCode=$null}
    }
    function script:Get-Module { param($ListAvailable) [pscustomobject]@{Name='WebAdministration'} }
    function script:Import-Module {}
    function script:Get-Website {
        for ($i=0;$i -lt $script:fixture.count;$i++) { [pscustomobject]@{Name=('SyntheticSite'+$i)} }
    }
    function script:Get-WebApplication {
        param($Site)
        for ($i=0;$i -lt $script:fixture.count;$i++) { [pscustomobject]@{Path=('/synthetic'+$i)} }
    }
    function script:Get-WebBinding {
        param($Name,$Protocol)
        for ($i=0;$i -lt $script:fixture.count;$i++) { [pscustomobject]@{bindingInformation=('*:443:synthetic'+$i+'.invalid')} }
    }
    function script:Get-ChildItem {
        param($Path)
        for ($i=0;$i -lt $script:fixture.count;$i++) {
            if ($Path -ceq 'IIS:\AppPools') { [pscustomobject]@{Name=('SyntheticPool'+$i)} }
            elseif ($Path -ceq 'Cert:\LocalMachine\My') {
                [pscustomobject]@{HasPrivateKey=$true;NotBefore=(Get-Date).AddDays(-1);NotAfter=(Get-Date).AddDays(1)
                    EnhancedKeyUsageList=@([pscustomobject]@{ObjectId=[pscustomobject]@{Value='1.3.6.1.5.5.7.3.1'}})
                    Thumbprint=('A'*40);Subject='CN=synthetic.invalid'
                    DnsNameList=@(for ($j=0;$j -lt $script:fixture.count;$j++) { [pscustomobject]@{Unicode=('synthetic'+$j+'.invalid')} })}
            } else { throw 'Unexpected fixture catalog probe.' }
        }
    }
} $fixture
try {
    foreach ($count in @(0,1,3)) {
        $fixture.count=$count
        $catalog=& $windows { Get-ScomSetupLocalCatalog }
        $catalog.currentAccount='SYNTHETIC\operator'
        foreach ($blocked in @($true,$false)) {
            $fixture.blocked=$blocked
            $adapters=@{
                LocalCatalog={ $catalog }.GetNewClosure()
                Prerequisites={
                    param($p)
                    [pscustomobject]@{ready=(-not $fixture.blocked);checks=@(
                        for ($j=0;$j -lt $fixture.count;$j++) {
                            [pscustomobject]@{name='Windows PowerShell 5.1';passed=(-not $fixture.blocked);remedy='Synthetic'}
                        }
                    )}
                }.GetNewClosure()
            }
            $response=Invoke-ScomSetupGuiRequest ([pscustomobject]@{action='Bootstrap'}) 'C:\synthetic-package' -Adapters $adapters
            if ($response.ok -eq $blocked -or $response.sites.Count -ne $count) { throw 'Fixture did not exercise expected backend shape.' }
            [IO.File]::WriteAllText((Join-Path $root ("bootstrap-$count-$blocked.json")),
                ($response | ConvertTo-Json -Depth 48 -Compress),[Text.UTF8Encoding]::new($false))
        }
    }
    # The backend may also legitimately return a sanitized error envelope with no catalog or checks.
    [IO.File]::WriteAllText((Join-Path $root 'bootstrap-error.json'),
        '{"ok":false,"code":"SETUP_GUI_BOOTSTRAP_PROBE_FAILED","approvalRequired":false}',[Text.UTF8Encoding]::new($false))
    Write-Output 'PASS: generated synthetic Bootstrap fixtures through actual local catalog projection and GUI backend JSON; zero/one/many nested arrays, blocked/pass and error envelope. No live probes or source access.'
} finally {
    Remove-Module ScomSetup.Gui
}
