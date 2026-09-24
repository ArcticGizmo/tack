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

public sealed class MachinePathPlannerTests
{
    private const string Shims = @"C:\Users\me\AppData\Local\tack\shims";

    [Fact]
    public void Prepends_to_machine_and_strips_from_user()
    {
        var plan = MachinePathPlanner.PlaceFront(
            machinePath: @"C:\Windows;C:\Program Files\nodejs",
            userPath: $@"{Shims};C:\Users\me\bin",
            shimsDir: Shims);

        Assert.Equal($@"{Shims};C:\Windows;C:\Program Files\nodejs", plan.NewMachinePath);
        Assert.Equal(@"C:\Users\me\bin", plan.NewUserPath); // shims removed from user PATH
    }

    [Fact]
    public void Moves_shims_to_the_front_when_it_was_lower_in_machine_path()
    {
        var plan = MachinePathPlanner.PlaceFront($@"C:\Windows;{Shims}", null, Shims);
        Assert.Equal($@"{Shims};C:\Windows", plan.NewMachinePath);
        Assert.False(plan.UserChanged);
    }

    [Fact]
    public void No_change_when_shims_already_leads_and_absent_from_user()
    {
        var plan = MachinePathPlanner.PlaceFront($@"{Shims};C:\Windows", @"C:\Users\me\bin", Shims);
        Assert.False(plan.AnyChange);
        Assert.Null(plan.NewMachinePath);
        Assert.Null(plan.NewUserPath);
    }

    [Fact]
    public void Matches_paths_case_insensitively_and_ignores_trailing_slashes()
    {
        var plan = MachinePathPlanner.PlaceFront($@"C:\Windows;{Shims.ToUpperInvariant()}\", null, Shims);
        Assert.Equal($@"{Shims};C:\Windows", plan.NewMachinePath);
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
