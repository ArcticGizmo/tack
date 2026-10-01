using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Tack.Core.Platform;

/// <summary>
/// Who besides you can put a file in a folder, read from its ACL alone - never by writing a probe file, which is
/// both a side effect and the kind of thing endpoint protection objects to. The same rules as
/// <c>tools/audit-path.ps1</c>: an account can plant a file if it's allowed to add files, or to change the folder's
/// permissions or owner (and so give itself that right), or if it owns the folder. You, SYSTEM, Administrators and
/// TrustedInstaller don't count: those already run as you, or above you.
///
/// <para>Why it matters (ADR 0002): tack's shims and binaries are on your user PATH, so anyone who can write those
/// folders gets their code run in your sessions, elevated ones included. Under %LOCALAPPDATA% only you can,
/// unless something has changed the ACLs or redirected the folder to a share.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class FolderAccess
{
    private const FileSystemRights PlantRights =
        FileSystemRights.CreateFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Generic bits as stored in an ACE; FileSystemRights has no names for them.
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",      // SYSTEM
        "S-1-5-32-544",  // BUILTIN\Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // NT SERVICE\TrustedInstaller
        "S-1-3-4",       // OWNER RIGHTS: means whoever owns it, which is checked separately
    };

    /// <summary>The accounts other than you, SYSTEM, Administrators and TrustedInstaller that can put a file in
    /// <paramref name="dir"/>, by name where it resolves. Empty when only those can, or when the folder doesn't
    /// exist; null when the ACL can't be read.</summary>
    public static List<string>? OtherWriters(string dir)
    {
        if (!Directory.Exists(dir)) return new List<string>();

        DirectorySecurity acl;
        try { acl = new DirectoryInfo(dir).GetAccessControl(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException) { return null; }

        string? me = WindowsIdentity.GetCurrent().User?.Value;
        bool Mine(string sid) => sid == me || Trusted.Contains(sid);

        var allow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var deny = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            // Inherit-only entries (CREATOR OWNER, say) apply to what's created inside, not to this folder.
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            string sid = rule.IdentityReference.Value;
            var bucket = rule.AccessControlType == AccessControlType.Deny ? deny : allow;
            bucket[sid] = (bucket.TryGetValue(sid, out var m) ? m : 0) | Expand((int)rule.FileSystemRights);
        }

        var writers = new List<string>();
        foreach (var (sid, rights) in allow)
        {
            int effective = rights & ~(deny.TryGetValue(sid, out var d) ? d : 0);
            if (!Mine(sid) && (effective & (int)PlantRights) != 0)
                writers.Add(Name(sid));
        }

        // An owner can always rewrite the ACL, whatever it says now.
        if (acl.GetOwner(typeof(SecurityIdentifier))?.Value is { } owner && !Mine(owner) && !writers.Contains(Name(owner)))
            writers.Add(Name(owner) + " (owner)");
        return writers;
    }

    private static int Expand(int mask)
    {
        if ((mask & GenericAll) != 0) mask |= (int)PlantRights;
        if ((mask & GenericWrite) != 0) mask |= (int)FileSystemRights.CreateFiles;
        return mask;
    }

    private static string Name(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { return sid; }
    }
}
