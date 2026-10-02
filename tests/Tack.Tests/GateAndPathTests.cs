using Tack.Core;
using Tack.Core.Config;
using Tack.Core.Maintenance;
using Tack.Core.Platform;
using Tack.Core.Resolution;
using Xunit;

namespace Tack.Tests;

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

    private const string Install = @"C:\Users\me\AppData\Local\Tack\current";

    [Fact]
    public void Register_prepends_shims_and_appends_install_dir()
    {
        var result = PathEdits.Register(@"C:\Windows;C:\Program Files\nodejs", Shims, Install);
        Assert.Equal($@"{Shims};C:\Windows;C:\Program Files\nodejs;{Install}", result);
    }

    [Fact]
    public void Register_promotes_shims_and_keeps_an_existing_install_dir_where_it_is()
    {
        var result = PathEdits.Register($@"C:\Windows;{Install};{Shims}", Shims, Install);
        Assert.Equal($@"{Shims};C:\Windows;{Install}", result);
    }

    [Fact]
    public void Register_returns_null_when_already_wired()
    {
        Assert.Null(PathEdits.Register($@"{Shims};C:\Windows;{Install}\", Shims, Install));
    }

    [Fact]
    public void Register_preserves_env_tokens_of_untouched_entries()
    {
        string expand(string p) => p.Replace("%SystemRoot%", @"C:\WINDOWS", StringComparison.OrdinalIgnoreCase);
        var result = PathEdits.Register(@"%SystemRoot%\system32;%SystemRoot%", Shims, Install, expand: expand);
        Assert.Equal($@"{Shims};%SystemRoot%\system32;%SystemRoot%;{Install}", result);
    }

    [Fact]
    public void Register_without_an_install_dir_only_places_the_shims()
    {
        // A dev build runs from its build output, so it puts nothing but its shims on the PATH.
        Assert.Equal($@"{Shims};C:\Windows", PathEdits.Register(@"C:\Windows", Shims, installDir: null));
        Assert.Null(PathEdits.Register($@"{Shims};C:\Windows", Shims, installDir: null));
    }

    [Fact]
    public void Register_can_place_the_shims_behind_another_dir()
    {
        const string release = @"C:\Users\me\AppData\Local\tack\shims";
        const string dev = @"C:\Users\me\AppData\Local\tack (Dev)\shims";
        // Dev only ever moves its own entry: wherever the release shims are, it goes directly after them.
        Assert.Equal($@"C:\scoop\shims;{release};{dev};C:\Windows",
            PathEdits.Register($@"C:\scoop\shims;{release};C:\Windows", dev, installDir: null, behind: new[] { release }));
    }

    [Fact]
    public void Remove_drops_only_the_named_dirs()
    {
        var result = PathEdits.Remove($@"{Shims};%SystemRoot%\system32;{Install}\", new[] { Shims, Install },
            p => p.Replace("%SystemRoot%", @"C:\WINDOWS", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"%SystemRoot%\system32", result);
    }

    [Fact]
    public void Remove_returns_null_when_nothing_matched()
    {
        Assert.Null(PathEdits.Remove(@"C:\Windows;C:\nodejs", new[] { Shims, Install }));
    }
}

public sealed class PathDoctorDisabledTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-doctor-dis-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Reports_disabled_and_still_checks_the_path()
    {
        // Disabling is a setting; the shims stay on the user PATH, so their checks still apply.
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node" } } } } },
            Settings = { Disabled = true },
        };

        var report = PathDoctor.Run(config, shims, FakeSearch.Of(null, shims));

        Assert.Contains(report.Checks, c => c.Title == "tack is disabled" && c.Status == CheckStatus.Warn);
        Assert.Contains(report.Checks, c => c.Title == "Shims directory is on your user PATH" && c.Status == CheckStatus.Ok);
    }

    [Fact]
    public void Enabled_config_reports_no_disabled_warning()
    {
        string shims = Directory.CreateDirectory(Path.Combine(_root, "shims")).FullName;
        var report = PathDoctor.Run(new CentralConfig(), shims, FakeSearch.Of(null, shims));
        Assert.DoesNotContain(report.Checks, c => c.Title == "tack is disabled");
    }
}

public sealed class DevBehindReleaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-devrel-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Release = @"C:\Users\me\AppData\Local\tack\shims";
    private const string Dev = @"C:\Users\me\AppData\Local\tack (Dev)\shims";

    [Fact]
    public void PromoteFront_puts_dev_directly_behind_the_release_shims()
    {
        var result = PathEdits.PromoteFront($@"{Release};C:\Program Files\nodejs;C:\Windows", Dev, new[] { Release });
        Assert.Equal($@"{Release};{Dev};C:\Program Files\nodejs;C:\Windows", result);
    }

    [Fact]
    public void PromoteFront_moves_dev_up_from_lower_in_the_path()
    {
        var result = PathEdits.PromoteFront($@"{Release};C:\nodejs;{Dev}", Dev, new[] { Release });
        Assert.Equal($@"{Release};{Dev};C:\nodejs", result);
    }

    [Fact]
    public void PromoteFront_goes_to_the_very_front_with_no_release_on_path()
    {
        var result = PathEdits.PromoteFront(@"C:\nodejs;C:\Windows", Dev, new[] { Release });
        Assert.Equal($@"{Dev};C:\nodejs;C:\Windows", result);
    }

    [Fact]
    public void PromoteFront_returns_null_when_dev_already_sits_behind_release()
    {
        Assert.Null(PathEdits.PromoteFront($@"{Release};{Dev};C:\Windows", Dev, new[] { Release }));
    }

    [Fact]
    public void Release_prepend_still_takes_the_front_ahead_of_dev()
    {
        Assert.Equal($@"{Release};{Dev};C:\Windows", PathEdits.PrependFront($@"{Dev};C:\Windows", Release));
    }

    [Fact]
    public void Passthrough_continues_after_its_own_entry()
    {
        // The release shim hands over to the dev shim; the dev shim never goes back to the release one.
        string path = $@"{Release};C:\nodejs;{Dev};C:\other";
        Assert.Equal(new[] { @"C:\nodejs", Dev, @"C:\other" }, PassthroughScan.Candidates(path, Release));
        Assert.Equal(new[] { @"C:\other" }, PassthroughScan.Candidates(path, Dev));
    }

    [Fact]
    public void Passthrough_off_path_scans_everything_but_itself()
    {
        Assert.Equal(new[] { Release, @"C:\x" }, PassthroughScan.Candidates($@"{Release};C:\x", Dev));
    }

    [Fact]
    public void Passthrough_matches_its_own_entry_despite_a_trailing_slash()
    {
        Assert.Equal(new[] { @"C:\x" }, PassthroughScan.Candidates($@"{Release};{Dev}\;C:\x", Dev));
    }

    [Fact]
    public void Doctor_reports_a_release_tack_ahead_as_a_handover_not_shadowing()
    {
        string release = Directory.CreateDirectory(Path.Combine(_root, "tack", "shims")).FullName;
        string dev = Directory.CreateDirectory(Path.Combine(_root, "tack (Dev)", "shims")).FullName;
        File.WriteAllText(Path.Combine(release, "node.exe"), "SHIM");
        var config = new CentralConfig
        {
            Tools = { ["node"] = new RegisteredTool { Versions = { ["1"] = new InstalledVersion { BinDir = _root, Exposes = { "node" } } } } },
        };

        var report = PathDoctor.Run(config, dev,
            FakeSearch.Of(null, $"{release};{dev}", Path.Combine(release, "node.exe")),
            tackShimsDirs: new[] { release, dev });

        Assert.Contains(report.Checks, c => c.Title == "Behind another tack instance" && c.Detail.Contains("node"));
        Assert.DoesNotContain(report.Checks, c => c.Title == "'node' isn't intercepted");
    }
}
