$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScomSdkReader.psm1') -Force

$root = [Guid]'00000000-0000-0000-0000-000000000001'
$nested = [Guid]'00000000-0000-0000-0000-000000000002'
$computer = [Guid]'00000000-0000-0000-0000-000000000003'
$network = [Guid]'00000000-0000-0000-0000-000000000004'
$queries = New-Object 'System.Collections.Generic.List[System.Guid]'
$source = {
    param($id, $limit)
    $queries.Add($id)
    if ($limit -ne 5) { throw 'Expected bounded callback limit.' }
    if ($id -eq $root) {
        return [pscustomobject]@{
            Complete = $true
            Members = @([pscustomobject]@{ Id = $nested; IsGroup = $true },
                [pscustomobject]@{ Id = $computer; IsGroup = $false })
        }
    }
    if ($id -eq $nested) {
        return [pscustomobject]@{
            Complete = $true
            Members = @([pscustomobject]@{ Id = $root; IsGroup = $true },
                [pscustomobject]@{ Id = $computer; IsGroup = $false },
                [pscustomobject]@{ Id = $network; IsGroup = $false })
        }
    }
    throw 'Unexpected group source query.'
}
$closure = Resolve-SCOMApprovedGroupMembership -GroupId $root -ReadDirectMembers $source `
    -ScopeVisibilityVerified $true -MaxObjects 4 -MaxGroups 2
if ($closure.ExpectedObjectIds.Count -ne 4 -or $closure.GroupIds.Count -ne 2 -or
    $queries.Count -ne 2 -or -not $closure.MembershipVerified -or $closure.SourceWorkBoundVerified) {
    throw 'Nested scope must deduplicate overlap/cycles without silently claiming source bounds.'
}
function Assert-Rejected([scriptblock] $Action, [string] $Code) {
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -ne $Code) { throw }; return }
    throw "Expected rejection: $Code"
}
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ReadDirectMembers $source -ScopeVisibilityVerified $false
} 'SCOM_GROUP_SCOPE_UNVERIFIED'
if ($queries.Count -ne 2) { throw 'Unverified scope must execute no source callbacks.' }
$empty = { param($id,$limit) [pscustomobject]@{ Complete = $true; Members = @() } }
$emptyGroup = Resolve-SCOMApprovedGroupMembership -GroupId $root -ReadDirectMembers $empty -ScopeVisibilityVerified $true
if ($emptyGroup.ExpectedObjectIds.Count -ne 1) { throw 'Verified empty group retains the authorized group object.' }
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true `
        -ReadDirectMembers { [pscustomobject]@{ Complete = $false; Members = @() } }
} 'SCOM_GROUP_MEMBERSHIP_INCOMPLETE'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true `
        -ReadDirectMembers { [pscustomobject]@{ Complete = 'true'; Members = @() } }
} 'SCOM_GROUP_MEMBERSHIP_INCOMPLETE'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true -MaxObjects 1 `
        -ReadDirectMembers { [pscustomobject]@{ Complete = $true; Members = @([pscustomobject]@{ Id = $computer; IsGroup = $false }) } }
} 'SCOM_GROUP_MEMBER_BUDGET'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true -MaxGroups 1 `
        -ReadDirectMembers { [pscustomobject]@{ Complete = $true; Members = @([pscustomobject]@{ Id = $nested; IsGroup = $true }) } }
} 'SCOM_GROUP_NESTING_BUDGET'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true `
        -ReadDirectMembers { [pscustomobject]@{ Complete = $true; Members = @(
            [pscustomobject]@{ Id = $computer; IsGroup = $false },
            [pscustomobject]@{ Id = $computer; IsGroup = $false }) } }
} 'SCOM_GROUP_MEMBER_INVALID'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true `
        -ReadDirectMembers { [pscustomobject]@{ Complete = $true; Members = @([pscustomobject]@{ Id = $root; IsGroup = $false }) } }
} 'SCOM_GROUP_CLASSIFICATION_CONFLICT'
Assert-Rejected {
    Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true -DeadlineSeconds 1 `
        -ReadDirectMembers { Start-Sleep -Milliseconds 1100; [pscustomobject]@{ Complete = $true; Members = @() } }
} 'SCOM_GROUP_DEADLINE'
$next = Resolve-SCOMApprovedGroupMembership -GroupId $root -ScopeVisibilityVerified $true `
    -ReadDirectMembers { [pscustomobject]@{ Complete = $true; Members = @([pscustomobject]@{ Id = $network; IsGroup = $false }) } }
if ($next.ExpectedObjectIds -contains $computer -or $next.ExpectedObjectIds.Count -ne 2) {
    throw 'Next complete scope must reflect removed and added members, not retained discoveries.'
}
Write-Output 'Group scope validation passed: 11 synthetic cases; no live SDK queries or customer data.'
