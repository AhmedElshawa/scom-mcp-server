[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$installedModules = @(Get-Module -ListAvailable -Name OperationsManager)
$loadedModules = @(Get-Module -Name OperationsManager)
$commandNames = @('Get-SCOMGroup', 'Get-SCOMClassInstance', 'Get-SCOMAlert', 'Get-SCOMManagementPack')
$commands = @()
if ($loadedModules.Count -gt 0) {
    $commands = @(Get-Command -Module OperationsManager)
}
$commandReports = @(
    foreach ($name in $commandNames) {
        $matches = @($commands | Where-Object Name -eq $name)
        $sets = @(
            foreach ($command in $matches) {
                foreach ($set in $command.ParameterSets) {
                    [ordered]@{
                        name = $set.Name
                        parameters = @(
                            foreach ($parameter in $set.Parameters) {
                                [ordered]@{
                                    name = $parameter.Name
                                    type = $parameter.ParameterType.ToString()
                                    mandatory = $parameter.IsMandatory
                                    position = $parameter.Position
                                    fromPipeline = $parameter.ValueFromPipeline
                                }
                            }
                        )
                    }
                }
            }
        )
        [ordered]@{
            name = $name
            status = $(if ($matches.Count -gt 0) { 'metadataAvailable' } else { 'notLoadedOrNotFound' })
            parameterSets = $sets
            invocationVerified = $false
        }
    }
)

$candidateTypes = @(
    'IEntityObjectsManagement', 'IMonitoringObjectsManagement', 'EnterpriseManagementObject', 'MonitoringObject',
    'MonitoringPerformanceData', 'ObjectReader`1', 'IObjectReader`1', 'ObjectQueryOptions',
    'EnterpriseManagementObjectGenericCriteria', 'EnterpriseManagementRelationshipObjectGenericCriteria',
    'EnterpriseManagementRelationshipObject`1', 'MonitoringRelationshipObject', 'MonitoringAlert',
    'ManagementPackClass', 'ManagementPackRelationship', 'TraversalDepth', 'DerivedClassTraversalDepth'
)
$candidateMethods = @(
    'GetObjectReader', 'GetRelatedObjects', 'GetRelationshipObjects', 'GetRange', 'Refresh',
    'GetMonitoringPerformanceData', 'GetValues', 'GetRelationshipObjectsWhereSource',
    'GetRelationshipObjectsWhereTarget', 'GetLeastDerivedNonAbstractClass', 'GetMostDerivedClasses',
    'IsInstanceOf', 'GetProperties', 'GetMonitoringAlerts', 'GetMonitoringClass',
    'GetMonitoringRelationshipClass', 'GetBaseTypes', 'GetBaseType'
)
$assemblyReports = @(
    foreach ($assembly in [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -like 'Microsoft.EnterpriseManagement*' } |
        Sort-Object FullName) {
        $typeStatus = 'available'
        $types = @()
        try {
            $types = @($assembly.GetTypes())
        }
        catch [System.Reflection.ReflectionTypeLoadException] {
            $typeStatus = 'incomplete'
            $types = @($_.Exception.Types | Where-Object { $null -ne $_ })
        }
        $typeReports = @(
            foreach ($type in $types |
                Where-Object { $_.Namespace -like 'Microsoft.EnterpriseManagement*' -and
                    $_.Name -in $candidateTypes } |
                Sort-Object FullName) {
                $methods = @($type.GetMethods())
                foreach ($inherited in $type.GetInterfaces()) {
                    $methods += @($inherited.GetMethods())
                }
                $signatures = @($methods |
                    Where-Object { $_.Name -in $candidateMethods } |
                    ForEach-Object { $_.ToString() } |
                    Sort-Object -Unique)
                [ordered]@{
                    name = $type.FullName
                    constructors = @($type.GetConstructors() | ForEach-Object { $_.ToString() } | Sort-Object -Unique)
                    interfaces = @($type.GetInterfaces() | ForEach-Object FullName | Sort-Object)
                    candidateMethodSignatures = @($signatures | Select-Object -First 200)
                    signaturesTruncated = ($signatures.Count -gt 200)
                    candidateProperties = @($type.GetProperties() |
                        Where-Object { $_.Name -in @('PageSize', 'Count', 'Size', 'MaxCount', 'MaxResultCount',
                            'Id', 'Name', 'DisplayName', 'HealthState', 'SourceObject', 'TargetObject',
                            'RelationshipClass', 'MonitoringObjectId', 'ResolutionState', 'Severity',
                            'TimeRaised', 'LastModified', 'LastModifiedBy', 'Item', 'Value') } |
                        ForEach-Object { $_.ToString() } | Sort-Object -Unique)
                    enumValues = $(if ($type.IsEnum) { @([Enum]::GetNames($type)) } else { @() })
                    behaviorVerified = $false
                }
            }
        )
        [ordered]@{
            name = $assembly.GetName().Name
            assemblyVersion = $assembly.GetName().Version.ToString()
            typeInspection = $typeStatus
            types = $typeReports
        }
    }
)

$framework = [ordered]@{ status = 'notAvailable'; version = $null; release = $null }
if ($env:OS -eq 'Windows_NT') {
    $frameworkKey = 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'
    if (Test-Path -LiteralPath $frameworkKey) {
        $properties = Get-ItemProperty -LiteralPath $frameworkKey
        $framework = [ordered]@{
            status = 'available'
            version = $properties.Version
            release = $properties.Release
        }
    }
}

[ordered]@{
    schemaVersion = '1.0'
    certificationStatus = 'notCertified'
    metadataOnly = $true
    sourceQueriesExecuted = 0
    runtime = [ordered]@{
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        edition = $PSVersionTable.PSEdition
        clrVersion = [Environment]::Version.ToString()
        netFrameworkV4 = $framework
    }
    operationsManager = [ordered]@{
        installedModuleVersions = @($installedModules | ForEach-Object { $_.Version.ToString() } | Sort-Object -Unique)
        loadedModuleVersions = @($loadedModules | ForEach-Object { $_.Version.ToString() } | Sort-Object -Unique)
        commands = $commandReports
    }
    loadedSdkAssemblies = $assemblyReports
    unresolvedEvidence = @(
        'SCOM product build and update rollup',
        'Relevant management-pack versions and metric units',
        'Complete recursive membership visibility under the intended read-only role',
        'Source-scoped reader boundedness and continuation behavior',
        'Raw performance retrieval bounds and inclusive timestamp behavior',
        'Authoritative alert update/resolution reconciliation',
        'Measured source-load and freshness envelope'
    )
} | ConvertTo-Json -Depth 16
