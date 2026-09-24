using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Platform;
using Xunit;

namespace Tack.Tests;

public sealed class ShimGateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-gate-").FullName;
    private string Shims => Path.Combine(_root, "shims");
    private string Disabled => Path.Combine(_root, "shims_disabled");
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Disable_parks_the_shims_dir_and_enable_restores_it()
    {
        Directory.CreateDirectory(Shims);
        File.WriteAllText(Path.Combine(Shims, "node.exe"), "SHIM");

        var d = ShimGate.Disable(Shims, Disabled);
        Assert.Equal(GateAction.Disabled, d.Action);
        Assert.False(Directory.Exists(Shims));
        Assert.True(File.Exists(Path.Combine(Disabled, "node.exe"))); // content moved, not lost
        Assert.True(ShimGate.IsDisabled(Disabled));
        Assert.Equal(Disabled, ShimGate.ActiveDir(Shims, Disabled)); // writes target the parked dir

        var e = ShimGate.Enable(Shims, Disabled);
        Assert.Equal(GateAction.Enabled, e.Action);
        Assert.True(File.Exists(Path.Combine(Shims, "node.exe")));
        Assert.False(Directory.Exists(Disabled));
        Assert.False(ShimGate.IsDisabled(Disabled));
        Assert.Equal(Shims, ShimGate.ActiveDir(Shims, Disabled));
    }

    [Fact]
    public void Disable_without_an_existing_shims_dir_still_marks_disabled()
    {
        var d = ShimGate.Disable(Shims, Disabled);
        Assert.Equal(GateAction.Disabled, d.Action);
        Assert.True(Directory.Exists(Disabled));
        Assert.True(ShimGate.IsDisabled(Disabled));
    }

    [Fact]
    public void Disable_and_enable_are_idempotent()
    {
        Directory.CreateDirectory(Shims);
        ShimGate.Disable(Shims, Disabled);
        Assert.Equal(GateAction.AlreadyDisabled, ShimGate.Disable(Shims, Disabled).Action);

        ShimGate.Enable(Shims, Disabled);
        Assert.Equal(GateAction.AlreadyEnabled, ShimGate.Enable(Shims, Disabled).Action);
    }

    [Fact]
    public void Enable_reclaims_a_stray_empty_shims_dir_recreated_while_disabled()
    {
        Directory.CreateDirectory(Shims);
        File.WriteAllText(Path.Combine(Shims, "node.exe"), "SHIM");
        ShimGate.Disable(Shims, Disabled);
        Directory.CreateDirectory(Shims); // an installer recreated an empty shims dir while disabled

        var e = ShimGate.Enable(Shims, Disabled);
        Assert.Equal(GateAction.Enabled, e.Action);
        Assert.True(File.Exists(Path.Combine(Shims, "node.exe")));
        Assert.False(Directory.Exists(Disabled));
    }

    [Fact]
    public void Enable_refuses_to_clobber_a_non_empty_shims_dir()
    {
        Directory.CreateDirectory(Shims);
        ShimGate.Disable(Shims, Disabled);
        Directory.CreateDirectory(Shims);
        File.WriteAllText(Path.Combine(Shims, "real.exe"), "x"); // non-empty: don't silently destroy it

        var e = ShimGate.Enable(Shims, Disabled);
        Assert.Equal(GateAction.Conflict, e.Action);
        Assert.True(Directory.Exists(Disabled)); // left untouched for manual resolution
    }
}

public sealed class PathEditsTests
{
    private const string Shims = @"C:\Users\me\AppData\Local\tack\shims";

    [Fact]
    public void PrependFront_moves_shims_to_the_front()
    {
        var result = PathEdits.PrependFront(@"C:\Windows;C:\Program Files\nodejs", Shims);
        Assert.Equal($@"{Shims};C:\Windows;C:\Program Files\nodejs", result);
    }

    [Fact]
    public void PrependFront_dedupes_when_shims_was_lower_in_path()
    {
        var result = PathEdits.PrependFront($@"C:\Windows;{Shims}", Shims);
        Assert.Equal($@"{Shims};C:\Windows", result);
    }

    [Fact]
    public void PrependFront_returns_null_when_shims_already_leads()
    {
        Assert.Null(PathEdits.PrependFront($@"{Shims};C:\Windows", Shims));
    }

    [Fact]
    public void PrependFront_matches_case_insensitively_and_ignores_trailing_slashes()
    {
        var result = PathEdits.PrependFront($@"C:\Windows;{Shims.ToUpperInvariant()}\", Shims);
        Assert.Equal($@"{Shims};C:\Windows", result);
    }

    [Fact]
    public void PrependFront_preserves_env_tokens_of_untouched_entries()
    {
        // %SystemRoot% must survive verbatim; only the shims dir is added. The expander is injected so the
        // comparison knows %SystemRoot%\system32 and C:\WINDOWS\system32 are the same dir.
        string expand(string p) => p.Replace("%SystemRoot%", @"C:\WINDOWS", StringComparison.OrdinalIgnoreCase);
        var result = PathEdits.PrependFront(@"%SystemRoot%\system32;%SystemRoot%", Shims, expand);
        Assert.Equal($@"{Shims};%SystemRoot%\system32;%SystemRoot%", result); // tokens intact
    }

    [Fact]
    public void PrependFront_is_a_noop_even_when_a_tokenised_entry_already_leads()
    {
        string expand(string p) => p.Replace("%TACK_SHIMS%", Shims, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PathEdits.PrependFront($@"%TACK_SHIMS%;C:\Windows", Shims, expand));
    }

    [Fact]
    public void Remove_strips_shims_and_keeps_the_rest_verbatim()
    {
        var result = PathEdits.Remove($@"{Shims};%NVM_HOME%;C:\Users\me\bin", Shims);
        Assert.Equal(@"%NVM_HOME%;C:\Users\me\bin", result); // token preserved, shims gone
    }

    [Fact]
    public void Remove_returns_null_when_shims_is_absent()
    {
        Assert.Null(PathEdits.Remove(@"%NVM_HOME%;C:\Users\me\bin", Shims));
    }
}

public sealed class PathDoctorDisabledTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-doctor-dis-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Reports_disabled_and_skips_the_on_path_failure()
    {
        string shims = Path.Combine(_root, "shims"); // canonical, does not exist (parked)
        string parked = Directory.CreateDirectory(Path.Combine(_root, "shims_disabled")).FullName;
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node" } } } } },
        };

        var report = PathDoctor.Run(config, shims, _ => "", activeShimsDir: parked, disabled: true);

        Assert.Contains(report.Checks, c => c.Title == "tack is disabled" && c.Status == CheckStatus.Warn);
        Assert.DoesNotContain(report.Checks, c => c.Title == "Shims directory is on PATH");
    }
}
