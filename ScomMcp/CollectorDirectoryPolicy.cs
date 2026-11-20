using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ScomMcp;

// Control for the accepted hash-then-load window (docs/security-review-v0.2.md F3): the
// SHA-256 check and the PowerShell load are separate file opens, so the only thing that
// prevents a swap between them is that nobody but SYSTEM and Administrators can write to
// the collector directory or the script. This refuses to run otherwise.
internal static class CollectorPolicySettings
{
    // The validation harness turns this off for fixtures created under an arbitrary temp
    // directory and turns it back on for the checks of the policy itself.
    internal static bool Enforce = true;
}

[SupportedOSPlatform("windows")]
internal static class CollectorDirectoryPolicy
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private const int WriteRights = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership) | 0x10000000 /* GENERIC_ALL */ | 0x40000000 /* GENERIC_WRITE */;

    public static void Require(string scriptPath)
    {
        if (!CollectorPolicySettings.Enforce) return;
        var directory = new DirectoryInfo(Path.GetDirectoryName(scriptPath)!);
        FileSystemSecurity directorySecurity, fileSecurity;
        try
        {
            directorySecurity = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            fileSecurity = new FileInfo(scriptPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            throw new InvalidOperationException("SDK bridge collector directory permissions could not be read.", error);
        }
        if (FindViolation(directorySecurity, checkOwner: true) is not null || FindViolation(fileSecurity, checkOwner: false) is not null)
            throw new InvalidOperationException(
                "SDK bridge collector directory and script must be writable only by SYSTEM and Administrators.");
    }

    // The directory owner is checked because an owner can always rewrite the DACL. File owners are not:
    // files inherit the directory DACL, and only the grants decide who can write them.
    [SupportedOSPlatform("windows")]
    internal static string? FindViolation(FileSystemSecurity security, bool checkOwner = true)
    {
        if (checkOwner)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner is null || !IsTrusted(owner)) return "owner";
        }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!IsTrusted(sid) && ((int)rule.FileSystemRights & WriteRights) != 0) return sid.Value;
        }
        return null;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => sid.Equals(System) || sid.Equals(Administrators);
}
