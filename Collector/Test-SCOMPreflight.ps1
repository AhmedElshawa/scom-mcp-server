[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Metadata only: no connection, source queries, certificate access or writes.
$checks = New-Object 'System.Collections.Generic.List[object]'
function Add-PreflightCheck {
    param([string]$Code, [bool]$Passed, [string]$Action)
    $checks.Add([pscustomobject]@{
        code=$Code
        status=$(if ($Passed) { 'pass' } else { 'blocked' })
        action=$(if ($Passed) { $null } else { $Action })
    })
}

Add-PreflightCheck 'DESKTOP_POWERSHELL' ($PSVersionTable.PSEdition -eq 'Desktop' -and
    $PSVersionTable.PSVersion.Major -eq 5) 'Run in the approved Operations Manager Shell on the SDK host; do not bypass signing policy.'
$module = @(Get-Module -Name OperationsManager)
Add-PreflightCheck 'OPERATIONS_MANAGER_MODULE_LOADED' ($module.Count -gt 0) `
    'Open Operations Manager Shell with the intended worker account. This command does not import or install modules.'

$managerType='Microsoft.EnterpriseManagement.Monitoring.IMonitoringObjectsManagement' -as [type]
$objectType='Microsoft.EnterpriseManagement.Monitoring.MonitoringObject' -as [type]
$classType='Microsoft.EnterpriseManagement.Configuration.ManagementPackClass' -as [type]
$optionsType='Microsoft.EnterpriseManagement.Common.ObjectQueryOptions' -as [type]
$relationshipType='Microsoft.EnterpriseManagement.Configuration.ManagementPackRelationship' -as [type]
$derivedType='Microsoft.EnterpriseManagement.Configuration.DerivedClassTraversalDepth' -as [type]
$traversalType='Microsoft.EnterpriseManagement.Common.TraversalDepth' -as [type]
$typesLoaded=$null -ne $managerType -and $null -ne $objectType -and $null -ne $classType -and
    $null -ne $optionsType -and $null -ne $relationshipType -and $null -ne $derivedType -and $null -ne $traversalType
Add-PreflightCheck 'SDK_TYPES_LOADED' $typesLoaded 'Use the installed SCOM SDK host; missing metadata does not authorize downloading or installing software.'
$methods=@()
if ($typesLoaded) {
    $methods=@($managerType.GetMethods())
    foreach ($interface in $managerType.GetInterfaces()) { $methods+=@($interface.GetMethods()) }
    $methods=@($methods | Sort-Object { $_.ToString() } -Unique)
}
$classReader=@($methods | Where-Object {
    $p=$_.GetParameters()
    $_.Name -eq 'GetObjectReader' -and $_.IsGenericMethodDefinition -and
    $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 2 -and
    $p[0].ParameterType -eq $classType -and $p[1].ParameterType -eq $optionsType
})
$idReader=@($methods | Where-Object {
    $p=$_.GetParameters()
    $_.Name -eq 'GetObjectReader' -and $_.IsGenericMethodDefinition -and
    $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 2 -and
    $p[0].ParameterType -eq [System.Collections.Generic.ICollection[Guid]] -and $p[1].ParameterType -eq $optionsType
})
$relationshipReader=@($methods | Where-Object {
    $p=$_.GetParameters()
    $_.Name -eq 'GetRelationshipObjectsWhereSource' -and $_.IsGenericMethodDefinition -and
    $_.GetGenericArguments().Count -eq 1 -and $p.Count -eq 5 -and
    $p[0].ParameterType -eq [Guid] -and $p[1].ParameterType -eq $relationshipType -and
    $p[2].ParameterType -eq $derivedType -and $p[3].ParameterType -eq $traversalType -and
    $p[4].ParameterType -eq $optionsType
})
Add-PreflightCheck 'CLASS_READER_SIGNATURE' ($classReader.Count -eq 1) 'Installed class-reader overload is missing or ambiguous; review supported SDK compatibility.'
Add-PreflightCheck 'ID_READER_SIGNATURE' ($idReader.Count -eq 1) 'Installed ID-reader overload is missing or ambiguous; review supported SDK compatibility.'
Add-PreflightCheck 'SOURCE_RELATIONSHIP_SIGNATURE' ($relationshipReader.Count -eq 1) 'Installed source-scoped relationship overload is missing or ambiguous; review supported SDK compatibility.'
$propertyRetrieval=$null
if ($null -ne $optionsType) {
    $propertyRetrieval=$optionsType.GetProperty('DefaultPropertyRetrievalBehavior')
}
$allPropertiesSupported=$null -ne $propertyRetrieval -and $propertyRetrieval.CanWrite -and
    $propertyRetrieval.PropertyType.IsEnum -and
    'All' -cin [Enum]::GetNames($propertyRetrieval.PropertyType)
Add-PreflightCheck 'ALL_PROPERTY_RETRIEVAL_SUPPORTED' $allPropertiesSupported `
    'ID-scoped discovery requires writable DefaultPropertyRetrievalBehavior with All; identity-only null values cannot certify missing source data.'

[ordered]@{
    schemaVersion='1.0'
    status=$(if (@($checks | Where-Object status -EQ 'blocked').Count -gt 0) { 'blocked' } else { 'metadataReady' })
    certificationStatus='notCertified'
    metadataOnly=$true
    sourceQueriesExecuted=0
    configurationChanged=$false
    moduleVersions=@($module | ForEach-Object { $_.Version.ToString() } | Sort-Object -Unique)
    checks=$checks.ToArray()
    limitations=@(
        'Metadata-ready is not deployment-ready; no configuration, certificate, ACL or endpoint validation is performed.',
        'MaxResultCount is not a verified root-reader backend cap; leave its relationship-query default untouched. Relationship limits apply after materialization.',
        'No permissions, scope-visibility or verification flags are inferred or modified.'
    )
    requiredAcceptance=@(
        'Independent intended-account scope visibility and approval',
        'Installed reader behavior, source caps and measured collection load',
        'Management-pack property/unit/hosting and raw-query parity',
        'Protected installed configuration, dependencies, certificate and storage',
        'Publisher/reader authentication, TLS and Copilot Studio individual-user acceptance'
    )
} | ConvertTo-Json -Depth 8
