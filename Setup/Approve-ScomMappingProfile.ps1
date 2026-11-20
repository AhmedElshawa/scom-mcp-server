<#
.SYNOPSIS
  Shows the evidence behind a generated worker.json, asks for one APPROVE, and
  sets the six attestation flags to true.

.DESCRIPTION
  Run after New-ScomMappingProfile.ps1 exited 0. The script:
    1. prints the evidence summary written next to the worker file;
    2. refuses when the evidence is not Ready, when any unit is null, or when the
       worker file's mapping no longer matches the evidence (it was edited);
    3. asks you to type APPROVE once;
    4. sets exactly these six flags to true and changes nothing else:
         windowsMapping.verified, windowsMapping.agentMonitoringVerified,
         scopeVisibilityVerified, membershipQueryContractVerified,
         enumerationQueryContractVerified, additionalRelationshipContractVerified;
    5. writes <WorkerPath>.attestation.txt with the operator, the UTC date and
       the SHA-256 of the evidence file.

  The approval is an operator attestation that you reviewed the evidence. It is
  not proven by the SCOM SDK.
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory)] [string] $WorkerPath,
    [Parameter(ParameterSetName = 'Run')] [string] $EvidencePath,
    [Parameter(ParameterSetName = 'Run')] [string] $AttestationPath,
    [Parameter(ParameterSetName = 'Functions', Mandatory)] [switch] $LoadFunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ScomApprovalProperty {
    param($Object, [string] $Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Get-ScomApprovalComparable {
    # Key order and the PowerShell 5.1 JSON escaping of < > ' must not matter.
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string] -or $Value -is [ValueType]) { return ($Value | ConvertTo-Json -Compress) }
    if ($Value -is [System.Collections.IDictionary]) {
        $keys = @($Value.Keys | Sort-Object)
        return '{' + (($keys | ForEach-Object { ('"{0}":{1}' -f $_, (Get-ScomApprovalComparable $Value[$_])) }) -join ',') + '}'
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        return '[' + ((@($Value) | ForEach-Object { Get-ScomApprovalComparable $_ }) -join ',') + ']'
    }
    $keys = @($Value.PSObject.Properties | ForEach-Object Name | Sort-Object)
    '{' + (($keys | ForEach-Object { ('"{0}":{1}' -f $_, (Get-ScomApprovalComparable $Value.PSObject.Properties[$_].Value)) }) -join ',') + '}'
}

function Get-ScomApprovalProblems {
    param([Parameter(Mandatory)] $Worker, [Parameter(Mandatory)] $Evidence)
    $problems = New-Object System.Collections.Generic.List[string]
    $resolution = Get-ScomApprovalProperty $Evidence 'resolution'
    if ($null -eq $resolution) { $problems.Add('The evidence file has no resolution section.'); return @($problems) }
    if ((Get-ScomApprovalProperty $resolution 'status') -ne 'Ready') {
        $problems.Add("The evidence status is '$(Get-ScomApprovalProperty $resolution 'status')', not 'Ready'. Use the manual path in the deployment guide (6e/6f).")
    }
    $configuration = Get-ScomApprovalProperty (Get-ScomApprovalProperty $Worker 'profile') 'configuration'
    $mapping = Get-ScomApprovalProperty $configuration 'windowsMapping'
    if ($null -eq $mapping) { $problems.Add('The worker file has no profile.configuration.windowsMapping.'); return @($problems) }
    foreach ($name in 'memoryToMiB', 'volumeSizeToMiB', 'computerHostsOsRelationship', 'computerClassName', 'operatingSystemClassName', 'volumeClassName') {
        if ($null -eq (Get-ScomApprovalProperty $mapping $name)) { $problems.Add("windowsMapping.$name is null; a null value cannot be approved.") }
    }
    if ($problems.Count -gt 0) { return @($problems) }
    $expected = Get-ScomApprovalProperty $resolution 'windowsMapping'
    foreach ($name in 'verified', 'agentMonitoringVerified') {
        if ((Get-ScomApprovalProperty $mapping $name) -ne $false) { $problems.Add("windowsMapping.$name is not false; this file was already approved or edited.") }
    }
    foreach ($name in 'scopeVisibilityVerified', 'membershipQueryContractVerified', 'enumerationQueryContractVerified', 'additionalRelationshipContractVerified') {
        if ((Get-ScomApprovalProperty $configuration $name) -ne $false) { $problems.Add("$name is not false; this file was already approved or edited.") }
    }
    if ((Get-ScomApprovalComparable $mapping) -ne (Get-ScomApprovalComparable $expected)) {
        $problems.Add('windowsMapping in the worker file differs from the evidence; it was edited after generation. Regenerate it or use the manual path.')
    }
    if ((Get-ScomApprovalComparable (Get-ScomApprovalProperty $configuration 'allowedPropertiesByClass')) -ne (Get-ScomApprovalComparable (Get-ScomApprovalProperty $resolution 'allowedPropertiesByClass'))) {
        $problems.Add('allowedPropertiesByClass in the worker file differs from the evidence.')
    }
    if ((Get-ScomApprovalComparable @(Get-ScomApprovalProperty $configuration 'additionalRelationshipNames')) -ne (Get-ScomApprovalComparable @(Get-ScomApprovalProperty $resolution 'additionalRelationshipNames'))) {
        $problems.Add('additionalRelationshipNames in the worker file differs from the evidence.')
    }
    @($problems)
}

function Set-ScomMappingProfileApproval {
    # Sets exactly the six flags and returns the attestation text; no prompt, so validation can call it.
    param(
        [Parameter(Mandatory)] [string] $WorkerPath, [Parameter(Mandatory)] [string] $EvidencePath,
        [Parameter(Mandatory)] [string] $AttestationPath, [string] $Operator
    )
    $worker = [IO.File]::ReadAllText($WorkerPath) | ConvertFrom-Json
    $evidence = [IO.File]::ReadAllText($EvidencePath) | ConvertFrom-Json
    $problems = @(Get-ScomApprovalProblems -Worker $worker -Evidence $evidence)
    if ($problems.Count -gt 0) { throw ('Approval refused: ' + ($problems -join ' ')) }
    $configuration = $worker.profile.configuration
    $configuration.windowsMapping.verified = $true
    $configuration.windowsMapping.agentMonitoringVerified = $true
    $configuration.scopeVisibilityVerified = $true
    $configuration.membershipQueryContractVerified = $true
    $configuration.enumerationQueryContractVerified = $true
    $configuration.additionalRelationshipContractVerified = $true
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($WorkerPath, ($worker | ConvertTo-Json -Depth 20), $utf8)
    $hash = (Get-FileHash -LiteralPath $EvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $Operator) { $Operator = [Security.Principal.WindowsIdentity]::GetCurrent().Name }
    $text = @(
        'SCOM MCP windowsMapping operator attestation',
        "Operator: $Operator",
        "Approved (UTC): $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))",
        "Worker file: $WorkerPath",
        "Evidence file: $EvidencePath",
        "Evidence SHA-256: $hash",
        'Flags set to true: windowsMapping.verified, windowsMapping.agentMonitoringVerified, scopeVisibilityVerified, membershipQueryContractVerified, enumerationQueryContractVerified, additionalRelationshipContractVerified',
        'This is an operator attestation that the evidence was reviewed. It is not proven by the SCOM SDK.'
    ) -join "`r`n"
    [IO.File]::WriteAllText($AttestationPath, $text + "`r`n", $utf8)
    $text
}

if ($PSCmdlet.ParameterSetName -eq 'Functions') { return }

function Stop-ScomScript {
    param([string] $Message, [int] $Code)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit $Code
}

try {
    if (-not (Test-Path -LiteralPath $WorkerPath -PathType Leaf)) { Stop-ScomScript "Worker file not found: $WorkerPath" 1 }
    $WorkerPath = (Resolve-Path -LiteralPath $WorkerPath).ProviderPath
    if (-not $EvidencePath) { $EvidencePath = "$WorkerPath.evidence.json" }
    if (-not $AttestationPath) { $AttestationPath = "$WorkerPath.attestation.txt" }
    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) { Stop-ScomScript "Evidence file not found: $EvidencePath. Run New-ScomMappingProfile.ps1 first." 1 }
    $evidence = [IO.File]::ReadAllText($EvidencePath) | ConvertFrom-Json
    $worker = [IO.File]::ReadAllText($WorkerPath) | ConvertFrom-Json
    $problems = @(Get-ScomApprovalProblems -Worker $worker -Evidence $evidence)
    @($evidence.summaryLines) | ForEach-Object { Write-Host $_ }
    if ($problems.Count -gt 0) { Stop-ScomScript ('Approval refused: ' + ($problems -join ' ')) 1 }
    Write-Host ''
    Write-Host 'Approving sets six attestation flags to true in the worker file:'
    Write-Host '  windowsMapping.verified, windowsMapping.agentMonitoringVerified, scopeVisibilityVerified,'
    Write-Host '  membershipQueryContractVerified, enumerationQueryContractVerified, additionalRelationshipContractVerified'
    Write-Host 'You attest that you reviewed the evidence above and that allowedPropertiesByClass matches the receiver settings.'
    $answer = Read-Host 'Type APPROVE to continue (anything else cancels)'
    if ($answer -cne 'APPROVE') { Write-Host 'Cancelled. Nothing was changed.'; exit 3 }
    $text = Set-ScomMappingProfileApproval -WorkerPath $WorkerPath -EvidencePath $EvidencePath -AttestationPath $AttestationPath
    Write-Host ''
    Write-Host $text
    Write-Host "Wrote: $AttestationPath"
    Write-Host 'Result: APPROVED. Next: fill the identity fields, then run the setup Configure step.'
    exit 0
}
catch { Stop-ScomScript $_.Exception.Message 1 }
