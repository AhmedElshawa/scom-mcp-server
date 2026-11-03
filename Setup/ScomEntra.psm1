Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSetup.psm1')

function Invoke-ScomEntraProvisioning {
    param(
        [Parameter(Mandatory)][string]$TenantId,
        [Parameter(Mandatory)][string]$NamePrefix,
        [Parameter(Mandatory)]$Certificate,
        [Parameter(Mandatory)][string]$JournalPath,
        [Parameter(Mandatory)][scriptblock]$Graph,
        [scriptblock]$Approve = ${function:Confirm-ScomSetupEffect},
        [string]$ApiObjectId, [string]$PublisherObjectId, [string]$ReaderObjectId,
        [switch]$GrantPublisherRole
    )
    $tenant = [guid]::Empty
    if (-not [guid]::TryParseExact($TenantId,'D',[ref]$tenant) -or $tenant -eq [guid]::Empty -or
        $NamePrefix -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$') { throw 'ENTRA_INPUT_INVALID' }
    if ($Certificate.NotAfter -le (Get-Date) -or $Certificate.NotBefore -gt (Get-Date) -or
        $Certificate.Thumbprint -notmatch '^[0-9a-fA-F]{40}$') { throw 'ENTRA_CERTIFICATE_VALIDITY_REQUIRED' }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($Certificate)
    if (-not $rsa -or $rsa.KeySize -lt 2048) { throw 'ENTRA_RSA2048_PUBLIC_CERTIFICATE_REQUIRED' }
    $rsa.Dispose()
    $ids = @($ApiObjectId,$PublisherObjectId,$ReaderObjectId) | Where-Object { $_ }
    foreach ($id in $ids) {
        $value = [guid]::Empty
        if (-not [guid]::TryParseExact($id,'D',[ref]$value) -or $value -eq [guid]::Empty) { throw 'ENTRA_EXACT_OBJECT_ID_REQUIRED' }
    }
    if (@($ids | Select-Object -Unique).Count -ne @($ids).Count) { throw 'ENTRA_APP_IDENTITY_COLLISION' }
    $journal = if (Test-Path -LiteralPath $JournalPath) { Read-ScomSetupJson $JournalPath }
        else { [pscustomobject]@{ schemaVersion=1; tenantId=$TenantId; namePrefix=$NamePrefix; certificateThumbprint=$Certificate.Thumbprint; pending=$null; events=@() } }
    if ($journal.schemaVersion -ne 1 -or $journal.tenantId -ne $TenantId -or $journal.namePrefix -ne $NamePrefix -or
        $journal.certificateThumbprint -ne $Certificate.Thumbprint) { throw 'ENTRA_JOURNAL_POLICY_CHANGED' }
    if ($journal.pending) { throw 'ENTRA_INTERRUPTED_MUTATION_REVIEW_GRAPH_AND_JOURNAL_NO_BLIND_RETRY' }

    function Read-Graph {
        param([string]$Path)
        & $Graph 'GET' $Path $null
    }
    function Write-Graph {
        param([string]$Path, $Body, [string]$Effect)
        if (-not (& $Approve $Effect)) { throw 'ENTRA_CANCELLED_NO_MUTATION' }
        $journal.pending = [pscustomobject]@{ path=$Path; effect=$Effect }
        Save-ScomSetupJson $JournalPath $journal
        try {
            $result = & $Graph 'POST' $Path $Body
            if (-not $result.id) { throw 'ENTRA_GRAPH_MUTATION_RESPONSE_ID_REQUIRED' }
            $journal.events += [pscustomobject]@{path=$Path; objectId=$result.id; effect=$Effect}
            $journal.pending = $null
            Save-ScomSetupJson $JournalPath $journal
            $result
        }
        catch { Write-Warning 'ENTRA_MUTATION_FAILED_OR_INTERRUPTED. Journal retained; no automatic deletes or consent rollback.'; throw }
    }
    function Get-App {
        param([string]$ObjectId, [string]$Name, $Body)
        if ($ObjectId) {
            return Read-Graph "/applications/$ObjectId`?`$select=id,appId,displayName,signInAudience,identifierUris,api,appRoles,keyCredentials,requiredResourceAccess,web"
        }
        # Exact names detect interrupted creates, never prove ownership.
        $found = Read-Graph "/applications?`$filter=displayName eq '$Name'&`$select=id&`$top=2"
        if ($found.value.Count -ne 0) { throw 'ENTRA_EXISTING_NAME_SUPPLY_REVIEWED_OBJECT_ID_NO_AUTOMATIC_REUSE' }
        Write-Graph '/applications' $Body "Create dedicated single-tenant app '$Name'. No secret or broad Graph runtime permissions."
    }
    function Get-ServicePrincipal {
        param($App)
        $found = Read-Graph "/servicePrincipals?`$filter=appId eq '$($App.appId)'&`$select=id,appId,appRoles&`$top=2"
        if ($found.value.Count -gt 1) { throw 'ENTRA_AMBIGUOUS_SERVICE_PRINCIPAL' }
        if ($found.value.Count -eq 1) { return $found.value[0] }
        Write-Graph '/servicePrincipals' @{appId=$App.appId} "Create enterprise service principal for reviewed app $($App.appId)."
    }
    $scope = @{
        id=[guid]::NewGuid().ToString('D'); value='Scom.Read'; type='Admin'; isEnabled=$true
        adminConsentDisplayName='Read approved SCOM data'; adminConsentDescription='Read authorized retained SCOM data as the signed-in user.'
        userConsentDisplayName='Read approved SCOM data'; userConsentDescription='Read authorized retained SCOM data.'
    }
    $role = @{
        id=[guid]::NewGuid().ToString('D'); value='Scom.Ingest'; isEnabled=$true; allowedMemberTypes=@('Application')
        displayName='Publish approved SCOM datasets'; description='Publish approved bounded SCOM datasets to this API.'
    }
    # appId is assigned by Graph; create first, then refuse to patch a reused API.
    $api = Get-App $ApiObjectId "$NamePrefix-api" @{
        displayName="$NamePrefix-api"; signInAudience='AzureADMyOrg'
        api=@{requestedAccessTokenVersion=2; oauth2PermissionScopes=@($scope)}; appRoles=@($role)
    }
    $api = Read-Graph "/applications/$($api.id)?`$select=id,appId,signInAudience,identifierUris,api,appRoles"
    $resource = "api://$($api.appId)"
    if (-not $ApiObjectId) {
        # Only this invocation's newly created API is patched; reused APIs are
        # read-only and incompatible contracts require separate admin review.
        if (@($api.identifierUris).Count -ne 0) { throw 'ENTRA_NEW_API_CHANGED_CONCURRENTLY' }
        if (-not (& $Approve "Set ONLY the newly created API $($api.id) identifier URI to $resource. No existing API is patched.")) { throw 'ENTRA_CANCELLED_NO_MUTATION' }
        $journal.pending = [pscustomobject]@{path="/applications/$($api.id)"; effect='Set new API identifier URI'}
        Save-ScomSetupJson $JournalPath $journal
        $null = & $Graph 'PATCH' "/applications/$($api.id)" @{identifierUris=@($resource)}
        $journal.events += [pscustomobject]@{path="/applications/$($api.id)"; objectId=$api.id; effect='Set new API identifier URI'}
        $journal.pending = $null
        Save-ScomSetupJson $JournalPath $journal
        $api = Read-Graph "/applications/$($api.id)?`$select=id,appId,signInAudience,identifierUris,api,appRoles"
    }
    $scopes = @($api.api.oauth2PermissionScopes | Where-Object value -CEQ 'Scom.Read')
    $roles = @($api.appRoles | Where-Object value -CEQ 'Scom.Ingest')
    if ($api.signInAudience -cne 'AzureADMyOrg' -or $api.api.requestedAccessTokenVersion -ne 2 -or
        $resource -cnotin $api.identifierUris -or $scopes.Count -ne 1 -or -not $scopes[0].isEnabled -or
        $scopes[0].type -cne 'Admin' -or $roles.Count -ne 1 -or -not $roles[0].isEnabled -or
        $roles[0].allowedMemberTypes.Count -ne 1 -or $roles[0].allowedMemberTypes[0] -cne 'Application') {
        throw 'ENTRA_EXISTING_API_CONTRACT_CONFLICT_NO_OVERWRITE'
    }
    $key = @{
        keyId=[guid]::NewGuid().ToString('D'); type='AsymmetricX509Cert'; usage='Verify'
        key=[Convert]::ToBase64String($Certificate.RawData)
        customKeyIdentifier=[Convert]::ToBase64String($Certificate.GetCertHash())
        startDateTime=$Certificate.NotBefore.ToUniversalTime().ToString('o')
        endDateTime=$Certificate.NotAfter.ToUniversalTime().ToString('o'); displayName='SCOM publisher public certificate'
    }
    $publisher = Get-App $PublisherObjectId "$NamePrefix-publisher" @{
        displayName="$NamePrefix-publisher"; signInAudience='AzureADMyOrg'; keyCredentials=@($key)
        requiredResourceAccess=@(@{resourceAppId=$api.appId; resourceAccess=@(@{id=$roles[0].id; type='Role'})})
    }
    $reader = Get-App $ReaderObjectId "$NamePrefix-reader" @{
        displayName="$NamePrefix-reader"; signInAudience='AzureADMyOrg'
        requiredResourceAccess=@(@{resourceAppId=$api.appId; resourceAccess=@(@{id=$scopes[0].id; type='Scope'})})
        web=@{redirectUris=@()}
    }
    foreach ($app in @($publisher,$reader)) {
        if ($app.signInAudience -cne 'AzureADMyOrg' -or $app.appId -eq $api.appId) { throw 'ENTRA_CLIENT_CONTRACT_CONFLICT' }
    }
    $publicKeys = @($publisher.keyCredentials | Where-Object {
        $_.type -ceq 'AsymmetricX509Cert' -and $_.usage -ceq 'Verify' -and
        $_.customKeyIdentifier -ceq $key.customKeyIdentifier -and [DateTime]$_.endDateTime -gt (Get-Date)
    })
    if ($publicKeys.Count -ne 1) { throw 'ENTRA_EXISTING_PUBLISHER_CERTIFICATE_CONFLICT_NO_KEY_OVERWRITE' }
    foreach ($pair in @(@($publisher,$roles[0].id,'Role'),@($reader,$scopes[0].id,'Scope'))) {
        $permission = @($pair[0].requiredResourceAccess | Where-Object resourceAppId -EQ $api.appId |
            ForEach-Object resourceAccess | Where-Object { $_.id -eq $pair[1] -and $_.type -ceq $pair[2] })
        if ($permission.Count -ne 1) { throw 'ENTRA_EXISTING_CLIENT_PERMISSION_CONFLICT_NO_OVERWRITE' }
    }
    $apiSp = Get-ServicePrincipal $api
    $publisherSp = Get-ServicePrincipal $publisher
    $null = Get-ServicePrincipal $reader
    $assignments = Read-Graph "/servicePrincipals/$($publisherSp.id)/appRoleAssignments?`$top=100"
    if ($assignments.PSObject.Properties['@odata.nextLink']) { throw 'ENTRA_ASSIGNMENT_LIST_BUDGET_REQUIRES_MANUAL_REVIEW' }
    $assignment = @($assignments.value | Where-Object { $_.resourceId -eq $apiSp.id -and $_.appRoleId -eq $roles[0].id })
    if ($assignment.Count -gt 1) { throw 'ENTRA_AMBIGUOUS_ROLE_ASSIGNMENT' }
    if ($assignment.Count -eq 0 -and $GrantPublisherRole) {
        # Check the role has propagated to the resource SP before granting.
        $visible = @(Read-Graph "/servicePrincipals/$($apiSp.id)?`$select=id,appRoles" |
            ForEach-Object appRoles | Where-Object { $_.id -eq $roles[0].id -and $_.isEnabled })
        if ($visible.Count -ne 1) { throw 'ENTRA_ROLE_NOT_PROPAGATED_RETRY_READ_ONLY_LATER' }
        $null = Write-Graph "/servicePrincipals/$($publisherSp.id)/appRoleAssignments" @{
            principalId=$publisherSp.id; resourceId=$apiSp.id; appRoleId=$roles[0].id
        } "Grant ONLY Scom.Ingest on API $($apiSp.id) to publisher $($publisherSp.id). This is app-only admin consent; no Graph or reader rights."
        $assignments = Read-Graph "/servicePrincipals/$($publisherSp.id)/appRoleAssignments?`$top=100"
        $assignment = @($assignments.value | Where-Object { $_.resourceId -eq $apiSp.id -and $_.appRoleId -eq $roles[0].id })
    }
    if ($assignment.Count -ne 1) { throw 'ENTRA_PUBLISHER_CONSENT_REQUIRED_NO_READY_OUTPUT' }
    $identity = [pscustomobject][ordered]@{
        schemaVersion=1; tenantId=$TenantId; tokenVersion=2; apiClientId=$api.appId; resource=$resource
        requiredScope='Scom.Read'; ingestionRole='Scom.Ingest'; publisherClientId=$publisher.appId
        publisherObjectId=$publisherSp.id; readerClientId=$reader.appId; certificateThumbprint=$Certificate.Thumbprint
    }
    Assert-ScomSetupIdentity $identity
    $identity
}

Export-ModuleMember -Function Invoke-ScomEntraProvisioning
