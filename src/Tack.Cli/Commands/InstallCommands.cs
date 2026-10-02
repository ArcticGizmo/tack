using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using Spectre.Console;
using Spectre.Console.Cli;
using Tack.Core;
using Tack.Core.Changelog;
using Tack.Core.Config;
using Tack.Core.Installs;
using Tack.Core.Resolution;

namespace Tack.Cli.Commands;

// ---- tool available ------------------------------------------------------------------------------

public sealed class ToolsAvailableSettings : CommandSettings
{
    [CommandArgument(0, "<tool>")]
    [Description("node or python.")]
    public string Tool { get; init; } = "";

    [CommandArgument(1, "[prefix]")]
    [Description("List every version in one line, e.g. 20 or 3.12 (pre-releases included).")]
    public string? Prefix { get; init; }

    [CommandOption("-a|--all")]
    [Description("List every version, including ones tack can't install here and why.")]
    public bool All { get; init; }

    [CommandOption("--refresh")]
    [Description("Fetch the list of versions again, even if tack read it in the last hour.")]
    public bool Refresh { get; init; }
}

/// <summary><c>tack tool available</c>: what <c>tool install</c> could install here, newest first, with what you
/// already have marked. Works offline from the cached list, and says how old it is.</summary>
public sealed class ToolsAvailableCommand : AsyncCommand<ToolsAvailableSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ToolsAvailableSettings settings)
    {
        if (ToolSources.Find(settings.Tool) is not { } source)
        {
            AnsiConsole.MarkupLine($"[red]tack can't install '{Markup.Escape(settings.Tool)}'.[/] It installs " +
                $"{Markup.Escape(string.Join(" and ", ToolSources.All.Select(s => s.Tool)))}.");
            return 1;
        }

        var arch = RuntimeInformation.OSArchitecture;
        IndexResult index;
        try
        {
            using var net = new HttpDownloader(ToolsInstallCommand.UserAgent());
            index = await ToolsInstallCommand.Spin($"Reading {source.Publisher}'s versions...", () =>
                new IndexCache(TackPaths.User.InstallsCacheDir).LoadAsync(source, net.GetStringAsync, settings.Refresh));
        }
        catch (InstallException e)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        var rows = Available.List(source, index.Versions, arch, settings.Prefix, settings.All);
        if (rows.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.Prefix is { Length: > 0 } p
                ? $"[yellow]{Markup.Escape(source.Publisher)} has nothing tack can install for {Markup.Escape(source.Tool)} {Markup.Escape(p)} on Windows {VersionSpec.ArchName(arch)}.[/] [grey](--all lists everything)[/]"
                : $"[yellow]{Markup.Escape(source.Publisher)} lists nothing tack can install on Windows {VersionSpec.ArchName(arch)}.[/]");
            return 0;
        }

        var registered = new TackEnvironment().Load().Tools.TryGetValue(source.Tool, out var tool)
            ? tool.Versions
            : new Dictionary<string, InstalledVersion>(StringComparer.OrdinalIgnoreCase);

        string Note(AvailableVersion r) => registered.TryGetValue(r.Version, out var iv)
            ? iv.Install is null ? "[grey]registered (tool add)[/]" : "[green]installed[/]"
            : r.Unavailable is { } why ? $"[grey]can't install: {Markup.Escape(why)}[/]"
            : r.PreRelease ? "[yellow]pre-release[/]"
            : "";
        bool anyNote = rows.Any(r => Note(r).Length > 0);

        var table = new Table().RoundedBorder();
        table.AddColumn(new TableColumn("version").NoWrap());
        if (source.HasLts) table.AddColumn("lts");
        if (anyNote) table.AddColumn("");
        foreach (var r in rows)
        {
            var cells = new List<string> { Markup.Escape(r.Version) };
            if (source.HasLts) cells.Add(r.Lts is { } lts ? Markup.Escape(lts) : "");
            if (anyNote) cells.Add(Note(r));
            table.AddRow(cells.ToArray());
        }

        string what = settings.All ? "every version" : settings.Prefix is { Length: > 0 } pre ? $"{pre}.x" : "the newest of each line";
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(source.Tool)} from {Markup.Escape(source.Publisher)} for Windows {VersionSpec.ArchName(arch)}: {Markup.Escape(what)}[/]");
        AnsiConsole.Write(table);
        if (index.Offline is not null)
            AnsiConsole.MarkupLine($"[yellow]offline: this is the list from {ToolsInstallCommand.Ago(index.CachedAt!.Value)}[/] [grey]({Markup.Escape(index.Offline)})[/]");
        else if (index.CachedAt is { } at)
            AnsiConsole.MarkupLine($"[grey]list fetched {ToolsInstallCommand.Ago(at)}; --refresh fetches it again[/]");
        AnsiConsole.MarkupLine($"[grey]install one with [green]tack tool install {Markup.Escape(source.Tool)}@<version>[/]" +
            (settings.Prefix is null && !settings.All ? $"; [green]tack tool available {Markup.Escape(source.Tool)} <line>[/] lists a whole line[/]" : "[/]"));
        return 0;
    }
}

// ---- tool install --------------------------------------------------------------------------------

public sealed class ToolsInstallSettings : CommandSettings
{
    [CommandArgument(0, "<tool@version>")]
    [Description("e.g. node@20, node@lts, python@3.12 or python@latest. A prefix installs its newest release.")]
    public string Spec { get; init; } = "";

    [CommandOption("--refresh")]
    [Description("Fetch the list of versions again, even if tack read it in the last hour.")]
    public bool Refresh { get; init; }

    public override ValidationResult Validate()
    {
        var s = ToolSpec.Parse(Spec);
        if (string.IsNullOrEmpty(s.Tool) || string.IsNullOrEmpty(s.Version))
            return ValidationResult.Error("Specify tool@version, e.g. node@20, node@lts or python@3.12");
        return ValidationResult.Success();
    }
}

/// <summary>
/// <c>tack tool install</c> (ADR 0003): resolve the spec against the source's index, download, check and unpack it
/// into <c>installs\&lt;tool&gt;\&lt;version&gt;</c>, then register it exactly as <c>tool add</c> would. Core does all of
/// it; this command shows progress, turns Ctrl-C into a clean cancel, and reports.
/// </summary>
public sealed class ToolsInstallCommand : AsyncCommand<ToolsInstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ToolsInstallSettings settings)
    {
        var spec = ToolSpec.Parse(settings.Spec);
        if (ToolSources.Find(spec.Tool) is not { } source)
        {
            AnsiConsole.MarkupLine($"[red]tack can't install '{Markup.Escape(spec.Tool)}'.[/] It installs " +
                $"{Markup.Escape(string.Join(" and ", ToolSources.All.Select(s => s.Tool)))}; for anything else, install it " +
                $"yourself and register it with [green]tack tool add {Markup.Escape(spec.Tool)}@<version> <binDir>[/].");
            return 1;
        }

        // Ctrl-C cancels cleanly (staging deleted, nothing registered) instead of killing us mid-unpack.
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await Install(source, spec.Version!, settings.Refresh, cancel.Token);
        }
        catch (InstallException e)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
            return 1;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]cancelled[/] [grey]- nothing was installed.[/]");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> Install(IToolSource source, string requested, bool refresh, CancellationToken cancel)
    {
        var env = new TackEnvironment();
        var arch = RuntimeInformation.OSArchitecture;
        using var net = new HttpDownloader(UserAgent());

        // 1. Which version.
        var index = await Spin($"Reading {source.Publisher}'s versions...", () =>
            new IndexCache(TackPaths.User.InstallsCacheDir).LoadAsync(source, net.GetStringAsync, refresh, cancel));
        if (index.Offline is not null)
            AnsiConsole.MarkupLine($"[yellow]using the list of versions from {Ago(index.CachedAt!.Value)}[/] [grey]({Markup.Escape(index.Offline)})[/]");
        var remote = VersionSpec.Resolve(source, index.Versions, requested, arch);
        string version = remote.Version;
        if (!string.Equals(requested.Trim(), version, StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(source.Tool)}@{Markup.Escape(requested.Trim())} ->[/] {Markup.Escape(version)}");

        // 2. Already there? (I8) A managed version is a no-op; one tack didn't install is never replaced.
        if (env.Load().Tools.TryGetValue(source.Tool, out var tool) && tool.Versions.TryGetValue(version, out var existing))
        {
            if (existing.Install is null)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(source.Tool)}@{Markup.Escape(version)} is already registered from " +
                    $"{Markup.Escape(existing.BinDir)}, which tack didn't install.[/] Remove it first with " +
                    $"[green]tack tool remove {Markup.Escape(source.Tool)}@{Markup.Escape(version)}[/], then install it again.");
                return 1;
            }
            if (Directory.Exists(existing.BinDir))
            {
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(source.Tool)} {Markup.Escape(version)} is already installed[/] " +
                    $"[grey]({Markup.Escape(existing.BinDir)})[/]");
                return 0;
            }
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(source.Tool)} {Markup.Escape(version)}'s folder is missing,[/] so tack is installing it again.");
        }

        // 3. The plan (Node's hashes are a separate file).
        string? checksums = remote.ChecksumsUrl is { } sums
            ? await Spin("Fetching checksums...", () => net.GetStringAsync(sums, cancel))
            : null;
        var plan = source.Plan(remote, arch, checksums);

        // 4. Download, check, unpack, place, post-install.
        var installer = new Installer(TackPaths.User.InstallsDir, net, new ProcessRunner());
        InstalledVersion installed = null!;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new DownloadedColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Downloading {Markup.Escape(source.Tool)} {Markup.Escape(version)}", maxValue: 1);
                installed = await installer.InstallAsync(plan, new Inline<InstallProgress>(p => Show(task, p)), cancel);
                task.Value = task.MaxValue;
            });

        // 5. Register, as tool add does. Loaded again so nothing changed during the download is lost.
        var config = env.Load();
        bool isDefault = ToolRegistry.Register(config, source.Tool, version, installed);
        env.Save(config);

        AnsiConsole.MarkupLine($"[green]installed[/] {Markup.Escape(source.Tool)}@{Markup.Escape(version)} -> {Markup.Escape(installed.BinDir)}");
        AnsiConsole.MarkupLine($"[grey]exposes:[/] {Markup.Escape(string.Join(", ", installed.Exposes))}");
        if (isDefault)
            AnsiConsole.MarkupLine($"[grey]it's the default {Markup.Escape(source.Tool)}, as the first version registered.[/]");
        Shims.Sync(env, config);
        ToolsAddCommand.Reach(env, config, installed.Exposes, OperatingSystem.IsWindows() ? CommandSearch.Current() : null);
        return 0;
    }

    // One progress bar through the stages: bytes while downloading, then indeterminate with the stage's name.
    private static void Show(ProgressTask task, InstallProgress p)
    {
        if (p.Stage == InstallStage.Downloading && p.Download is { } d)
        {
            if (d.Total is { } total && total > 0) task.MaxValue = total;
            else task.IsIndeterminate = true;
            task.Value = d.Received;
            return;
        }
        task.IsIndeterminate = true;
        task.Description = p.Stage switch
        {
            InstallStage.Verifying => "Checking the SHA-256",
            InstallStage.Unpacking => "Unpacking",
            InstallStage.Placing => "Moving into place",
            InstallStage.PostInstall => Markup.Escape(p.Detail ?? "Finishing"),
            _ => task.Description,
        };
    }

    internal static async Task<T> Spin<T>(string status, Func<Task<T>> work)
    {
        T result = default!;
        await AnsiConsole.Status().StartAsync(status, async _ => result = await work());
        return result;
    }

    internal static string Ago(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        return age.TotalMinutes < 90 ? Count(Math.Max(1, (int)age.TotalMinutes), "minute")
            : age.TotalHours < 48 ? Count((int)age.TotalHours, "hour")
            : Count((int)age.TotalDays, "day");

        static string Count(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }

    internal static string UserAgent() => $"tack/{VersionInfo.Of(Assembly.GetExecutingAssembly())} (+https://github.com/ArcticGizmo/tack)";

    // Reports on the caller's thread. Progress<T> posts to the thread pool, which can deliver stages out of order.
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
