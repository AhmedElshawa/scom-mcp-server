[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$NamePrefix,
    [Parameter(Mandatory)][string]$PublicCertificatePath,
    [Parameter(Mandatory)][string]$StateDirectory,
    [string]$ApiObjectId, [string]$PublisherObjectId, [string]$ReaderObjectId,
    [switch]$GrantPublisherRole
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.Windows.psm1')
Import-Module (Join-Path $PSScriptRoot 'ScomEntra.psm1')
Assert-ScomSetupPrivateDirectory $StateDirectory
Assert-ScomSetupPath $PublicCertificatePath
if ([IO.Path]::GetExtension($PublicCertificatePath) -ine '.cer' -or
    (Get-Item -LiteralPath $PublicCertificatePath).Length -gt 65536) { throw 'ENTRA_BOUNDED_PUBLIC_DER_CER_REQUIRED_NO_PFX' }
Import-Module Microsoft.Graph.Authentication -ErrorAction Stop
$context = Get-MgContext
if (-not $context -or $context.TenantId -ne $TenantId -or $context.AuthType -ne 'Delegated' -or
    $context.Environment -ne 'Global' -or 'Application.ReadWrite.All' -notin $context.Scopes -or
    ($GrantPublisherRole -and 'AppRoleAssignment.ReadWrite.All' -notin $context.Scopes)) {
    throw 'ENTRA_AUTHORIZED_GLOBAL_DELEGATED_GRAPH_CONTEXT_REQUIRED. Sign in separately through the approved tenant process; this script does not acquire permissions or consent to Graph.'
}
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($PublicCertificatePath))
if ($certificate.HasPrivateKey) { $certificate.Dispose(); throw 'ENTRA_PUBLIC_CERTIFICATE_ONLY' }
$lease = [IO.File]::Open((Join-Path $StateDirectory 'entra.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $graph = {
        param($method,$path,$body)
        if ($path -notmatch '^/(applications|servicePrincipals)(/|\?|$)') { throw 'ENTRA_GRAPH_PATH_NOT_APPROVED' }
        $parameters = @{Method=$method; Uri="https://graph.microsoft.com/v1.0$path"; OutputType='PSObject'}
        if ($body) { $parameters.Body = $body | ConvertTo-Json -Depth 32; $parameters.ContentType='application/json' }
        Invoke-MgGraphRequest @parameters -ErrorAction Stop
    }
    $identity = Invoke-ScomEntraProvisioning -TenantId $TenantId -NamePrefix $NamePrefix -Certificate $certificate `
        -JournalPath (Join-Path $StateDirectory 'entra-journal.json') -Graph $graph `
        -ApiObjectId $ApiObjectId -PublisherObjectId $PublisherObjectId -ReaderObjectId $ReaderObjectId `
        -GrantPublisherRole:$GrantPublisherRole
    if (-not (Confirm-ScomSetupEffect 'Save validated nonsecret identity.json in this private directory. No OAuth secret or private key is exported. Reader consent and Studio-generated callback/credential still require the authorized customer process.')) {
        throw 'ENTRA_CANCELLED_NO_OUTPUT_WRITE'
    }
    Save-ScomSetupJson (Join-Path $StateDirectory 'identity.json') $identity
    Write-Output 'Nonsecret identity saved. Configure the Studio-generated Web callback, protected OAuth credential and scoped delegated reader consent separately. No ARM deployment, DNS, firewall, certificate enrollment or live collector performed.'
}
finally { $lease.Dispose(); $certificate.Dispose() }
