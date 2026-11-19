$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Validate-ScomManagementGroupScope.ps1')
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force
$names=@('Synthetic.Windows.Computer','Synthetic.Windows.OS','Synthetic.Windows.Volume')
$classes=@(foreach ($name in $names) {
    $class=New-Object Microsoft.EnterpriseManagement.Configuration.ManagementPackClass
    $class.Name=$name
    $class
})
$mapping=[pscustomobject]@{
    verified=$true;agentMonitoringVerified=$true
    computerClassName=$names[0];operatingSystemClassName=$names[1];volumeClassName=$names[2]
    hostnameProperty='DnsName';osVersionProperty='Version';memoryProperty='Memory'
    processorCountProperty='Cpu';volumeNameProperty='Name';fileSystemProperty='FileSystem';volumeSizeProperty='Size'
    memoryToMiB=1;volumeSizeToMiB=1
    computerHostsOsRelationship='Synthetic.HostsOS';osHostsVolumeRelationship='Synthetic.HostsVolume'
}
$manager=New-Object Microsoft.EnterpriseManagement.Monitoring.ScopeManager
foreach ($name in @($names)+@('Synthetic.Linux','Synthetic.Network','Synthetic.Application')) {
    $row=New-Object Microsoft.EnterpriseManagement.Monitoring.MonitoringObject
    $row.Id=[Guid]::NewGuid();$row.Kind=$name
    $manager.Rows.Add($row)
}
$windowsArgs=@{
    EntityObjects=$manager;Classes=$classes;Mapping=$mapping
    WholeManagementGroupApproved=$true;ScopeVisibilityVerified=$true;EnumerationQueryContractVerified=$true
    MaxObjects=3;PageSize=1;DeadlineSeconds=10
}
$scope=Read-SCOMWindowsManagementGroupScope @windowsArgs
if ($scope.ExpectedObjectIds.Count -ne 3 -or $scope.MembershipObjects.Count -ne 3 -or
    ($manager.RequestedClasses -join ',') -cne ($names -join ',') -or $manager.PageCalls -ne 3 -or
    @($manager.RequestedLimits | Where-Object {$_ -ne 4}).Count -ne 0) {
    throw 'Supported Windows must query only the three reviewed classes using max+1 and bounded pages.'
}
$windowsArgs.MaxObjects=2
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_MANAGEMENT_GROUP_SCOPE_OVERFLOW'
$windowsArgs.MaxObjects=3
$mapping.verified=$false
$calls=$manager.Calls
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
if ($manager.Calls -ne $calls) {throw 'Unreviewed mapping touched SDK.'}
$mapping.verified=$true
$classes[0].Name='System.Entity'
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_WINDOWS_CLASS_MAPPING_INVALID'
$classes[0].Name=$names[0]
$windowsArgs.WholeManagementGroupApproved=$false
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_MANAGEMENT_GROUP_SCOPE_UNVERIFIED'
$windowsArgs.WholeManagementGroupApproved=$true
$manager.Mode='short'
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_MANAGEMENT_GROUP_PAGE_INCOMPLETE'
$manager.Mode='changed'
Assert-ScopeFault {Read-SCOMWindowsManagementGroupScope @windowsArgs} 'SCOM_MANAGEMENT_GROUP_READER_CHANGED_OR_DEADLINE'
$manager.Mode='slow';$windowsArgs.DeadlineSeconds=1
try { Read-SCOMWindowsManagementGroupScope @windowsArgs | Out-Null;throw 'Deadline ignored.' }
catch {if ($_.Exception.Message -notmatch '^SCOM_MANAGEMENT_GROUP_(READER_CHANGED_OR_DEADLINE|DEADLINE)$') {throw}}
$manager.Mode='';$windowsArgs.DeadlineSeconds=10
$roles=[pscustomobject]@{
    verified=$true;agentClassName='Synthetic.Agent';managementServerClassName='Synthetic.Server'
    computerHostsApplicationRelationship='Synthetic.HostsRole'
}
$mapping | Add-Member -NotePropertyName monitoringRoleMapping -NotePropertyValue $roles
$roleClasses=@(foreach ($name in @($roles.agentClassName,$roles.managementServerClassName)) {
    $class=New-Object Microsoft.EnterpriseManagement.Configuration.ManagementPackClass
    $class.Name=$name;$class
    $row=New-Object Microsoft.EnterpriseManagement.Monitoring.MonitoringObject
    $row.Id=[Guid]::NewGuid();$row.Kind=$name;$manager.Rows.Add($row)
})
$windowsArgs.Classes=@($classes)+@($roleClasses);$windowsArgs.MaxObjects=5
$manager.RequestedClasses.Clear()
$scope=Read-SCOMWindowsManagementGroupScope @windowsArgs
if ($scope.ExpectedObjectIds.Count -ne 5 -or $manager.RequestedClasses.Count -ne 5 -or
    $manager.RequestedClasses.Contains('System.Entity')) {throw 'Optional reviewed roles or exact scope failed.'}
$manager.Rows.Clear()
$instant=[DateTimeOffset]::UtcNow
$scanArgs=@{
    EntityObjects=$manager;ManagementGroupId=[Guid]::NewGuid();ScopeKind='managementGroup'
    InventoryKind='supportedWindows';WholeManagementGroupApproved=$true
    ScopeVisibilityVerified=$true;EnumerationQueryContractVerified=$true
    Request=@{requestedAtUtc=$instant.ToString('o');metricHistoryStartUtc=$instant.AddDays(-14).ToString('o');priorUnresolvedAlerts=@()}
    ReadActiveAlerts={param($object) @()};ReadAlertById={param($id) @()}
    WindowsMapping=$mapping;WindowsComputerClass=$classes[0];WindowsOsClass=$classes[1];WindowsVolumeClass=$classes[2]
    WindowsAgentClass=$roleClasses[0];WindowsManagementServerClass=$roleClasses[1]
    AdditionalRelationshipContractVerified=$true
}
$scan=Get-SCOMGenericScan @scanArgs
if ($scan.inventoryKind -cne 'supportedWindows' -or $null -ne $scan.groupId -or
    -not $scan.inventory.complete -or $scan.windowsInventory.devices.Count -ne 0) {
    throw 'Empty supported Windows scope/provenance failed.'
}
$scanArgs.WindowsMapping=$null;$calls=$manager.Calls
Assert-ScopeFault {Get-SCOMGenericScan @scanArgs} 'SCOM_WINDOWS_MAPPING_UNVERIFIED'
if ($manager.Calls -ne $calls) {throw 'Missing Windows mapping queried SDK.'}
Write-Output 'Supported Windows scope: reviewed three/five-class queries only, unrelated classes excluded, aggregate budgets, pages, deadlines, mapping/approval gates, empty scan and null groupId passed.'
