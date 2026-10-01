using Tack.Core.Resolution;
using Xunit;

namespace Tack.Tests;

/// <summary>A command search over made-up PATH values and files, with a Windows folder that holds only what a test
/// puts there.</summary>
internal static class FakeSearch
{
    public const string Windows = @"C:\FakeWindows";
    public const string System32 = @"C:\FakeWindows\System32";

    public static CommandSearch Of(string? system, string? user, params string[] files) => new()
    {
        Entries = CommandLookup.Entries(system, user, expand: p => p),
        Extensions = CommandLookup.Extensions(".COM;.EXE;.BAT;.CMD"),
        WindowsDir = Windows,
        SystemDir = System32,
        FileExists = p => files.Contains(p, StringComparer.OrdinalIgnoreCase),
    };
}

public sealed class CommandLookupTests
{
    private const string Shims = @"C:\Users\me\AppData\Local\tack\shims";

    [Fact]
    public void Entries_are_the_system_path_then_the_user_path_numbered_within_each()
    {
        var entries = CommandLookup.Entries(@"C:\a;C:\b", @"C:\c", expand: p => p);

        Assert.Equal(new[] { @"C:\a", @"C:\b", @"C:\c" }, entries.Select(e => e.Dir));
        Assert.Equal("system PATH entry 2", entries[1].Where);
        Assert.Equal("user PATH entry 1", entries[2].Where);
    }

    [Fact]
    public void An_earlier_folder_wins_whatever_the_extension()
    {
        // cmd tries every PATHEXT extension in a folder before the next folder, so an earlier node.cmd beats node.exe.
        var search = FakeSearch.Of(@"C:\nodejs", Shims, @"C:\nodejs\node.cmd", $@"{Shims}\node.exe");

        var hit = search.Shadowing("node", Shims);

        Assert.Equal(@"C:\nodejs\node.cmd", hit!.Exe);
        Assert.Equal(PathScope.System, hit.Entry.Scope);
    }

    [Fact]
    public void Within_a_folder_pathext_order_decides()
    {
        var hit = CommandLookup.Find("tool", CommandLookup.Entries(@"C:\x", null, p => p),
            CommandLookup.Extensions(".COM;.EXE"), p => p is @"C:\x\tool.exe" or @"C:\x\tool.com");
        Assert.Equal(@"C:\x\tool.com", hit!.Exe);
    }

    [Fact]
    public void Nothing_ahead_of_the_shims_means_no_shadow()
    {
        var search = FakeSearch.Of(@"C:\Windows", $@"{Shims};C:\nodejs", @"C:\nodejs\node.exe");
        Assert.Null(search.Shadowing("node", Shims));
    }

    [Fact]
    public void Shims_off_the_path_report_no_shadow()
    {
        // Not being on PATH at all is its own problem; doctor reports it separately.
        Assert.Null(FakeSearch.Of(@"C:\nodejs", null, @"C:\nodejs\node.exe").Shadowing("node", Shims));
    }

    [Fact]
    public void An_empty_pathext_falls_back_to_cmds_default()
    {
        Assert.Contains(".CMD", CommandLookup.Extensions(null));
        Assert.Contains(".EXE", CommandLookup.Extensions(" "));
    }
}

public sealed class CommandSearchTests
{
    private const string Shims = @"C:\Users\me\AppData\Local\tack\shims";

    [Fact]
    public void A_system_path_install_is_explained_as_something_to_move()
    {
        var search = FakeSearch.Of(@"C:\Program Files\nodejs", Shims, @"C:\Program Files\nodejs\node.exe");

        var shadow = search.Explain("node", Shims)!;

        Assert.Equal(ShadowKind.SystemPath, shadow.Kind);
        Assert.Contains("system PATH entry 1", shadow.Advice);
        Assert.Contains("off the system PATH", shadow.Advice);
    }

    [Fact]
    public void A_user_path_entry_ahead_is_fixed_by_doctor()
    {
        var search = FakeSearch.Of(null, $@"C:\scoop\shims;{Shims}", @"C:\scoop\shims\node.exe");

        var shadow = search.Explain("node", Shims)!;

        Assert.Equal(ShadowKind.UserPath, shadow.Kind);
        Assert.Contains("doctor --fix", shadow.Advice);
    }

    [Fact]
    public void A_windows_command_is_explained_as_out_of_reach()
    {
        var search = FakeSearch.Of($@"{FakeSearch.System32}", Shims, $@"{FakeSearch.System32}\curl.exe");

        Assert.Equal(ShadowKind.Windows, search.Explain("curl", Shims)!.Kind);
    }

    [Fact]
    public void Another_tack_instance_ahead_is_its_own_kind()
    {
        const string release = @"C:\Users\me\AppData\Local\tack\shims";
        const string dev = @"C:\Users\me\AppData\Local\tack (Dev)\shims";
        var search = FakeSearch.Of(null, $"{release};{dev}", $@"{release}\node.exe");

        Assert.Equal(ShadowKind.OtherTack, search.Explain("node", dev, new[] { release, dev })!.Kind);
    }

    [Theory]
    [InlineData(@"C:\FakeWindows\System32\where.exe")]   // System32: searched by CreateProcess, on PATH or not
    [InlineData(@"C:\FakeWindows\notepad.exe")]          // the Windows folder itself
    public void Windows_folders_own_their_commands_even_off_the_path(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        Assert.Equal(file, FakeSearch.Of(null, null, file).WindowsOwner(name));
    }

    [Fact]
    public void A_system_path_folder_under_windows_owns_its_commands()
    {
        const string openSsh = @"C:\FakeWindows\System32\OpenSSH";
        Assert.Equal($@"{openSsh}\ssh.exe", FakeSearch.Of(openSsh, null, $@"{openSsh}\ssh.exe").WindowsOwner("ssh"));
    }

    [Fact]
    public void Tools_outside_windows_are_not_windows_commands()
    {
        // Git ships its own ssh, but that one isn't Windows'; nor is anything on the user PATH.
        var search = FakeSearch.Of(@"C:\Program Files\Git\usr\bin", @"C:\FakeWindows\Tools",
            @"C:\Program Files\Git\usr\bin\ssh.exe", @"C:\FakeWindows\Tools\x.exe");

        Assert.Null(search.WindowsOwner("ssh"));
        Assert.Null(search.WindowsOwner("x"));
    }
}
