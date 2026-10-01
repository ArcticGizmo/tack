using Tack.Core.Platform;
using Xunit;

namespace Tack.Tests;

// Read-only: these only read ACLs on folders the test creates, never change them.
public sealed class FolderAccessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-acl-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void A_fresh_folder_in_your_profile_is_yours_alone()
    {
        // %TEMP% sits under %LOCALAPPDATA%, so a new folder there inherits the profile's ACL: you, SYSTEM, Administrators.
        if (!OperatingSystem.IsWindows()) return;
        Assert.Empty(FolderAccess.OtherWriters(_root)!);
    }

    [Fact]
    public void A_missing_folder_has_no_writers()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Empty(FolderAccess.OtherWriters(Path.Combine(_root, "nope"))!);
    }
}
