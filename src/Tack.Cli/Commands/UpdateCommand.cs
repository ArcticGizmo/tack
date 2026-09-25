using System.ComponentModel;
using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
using Velopack;
using Velopack.Sources;

namespace Tack.Cli.Commands;

// ---- update --------------------------------------------------------------------------------------

public sealed class UpdateSettings : CommandSettings
{
    [CommandOption("-c|--check")]
    [Description("Only report whether an update is available; don't install it.")]
    public bool Check { get; init; }
}

/// <summary>
/// Self-update from the GitHub releases feed via Velopack. Checks, downloads (with progress), then hands off
/// to the Velopack updater, which waits for this process to exit and swaps the install in place - silently,
/// with no restart (it's a CLI; the next <c>tack</c> you type is the new one). The post-update hook in
/// <see cref="InstallHook"/> then re-wires PATH and restamps shims with the new shim binary, exactly as an
/// install does. Only works in a Velopack install; a dev build (<c>run.bat</c>) is told so and bails.
/// </summary>
public sealed class UpdateCommand : AsyncCommand<UpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings settings)
    {
        string repoUrl = RepoUrl(Environment.GetEnvironmentVariable("TACK_REPO"), MetadataRepoUrl());
        var mgr = new UpdateManager(new GithubSource(repoUrl, accessToken: null, prerelease: false));

        if (!mgr.IsInstalled)
        {
            AnsiConsole.MarkupLine("[yellow]this tack isn't a Velopack install[/] (a dev build?), so it can't update itself.");
            AnsiConsole.MarkupLine("[grey]install a release with install.ps1, or rebuild from source.[/]");
            return 1;
        }

        string current = mgr.CurrentVersion?.ToString() ?? "unknown";
        try
        {
            UpdateInfo? info = null;
            await AnsiConsole.Status().StartAsync("Checking for updates...",
                async _ => info = await mgr.CheckForUpdatesAsync());

            if (info is null)
            {
                AnsiConsole.MarkupLine($"[green]tack v{Markup.Escape(current)} is up to date.[/]");
                return 0;
            }

            string next = info.TargetFullRelease.Version.ToString();
            if (settings.Check)
            {
                AnsiConsole.MarkupLine($"[yellow]update available:[/] v{Markup.Escape(current)} -> [green]v{Markup.Escape(next)}[/]");
                AnsiConsole.MarkupLine("[grey]run [green]tack update[/] to install it, and [green]tack changelog[/] afterwards to see what changed.[/]");
                return 0;
            }

            await AnsiConsole.Progress()
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Downloading v{Markup.Escape(next)}");
                    await mgr.DownloadUpdatesAsync(info, p => task.Value = p);
                    task.Value = 100;
                });

            // Launches the updater and tells it to wait for us to exit; returning below is that exit.
            mgr.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);

            AnsiConsole.MarkupLine($"[green]updating tack[/] v{Markup.Escape(current)} -> [green]v{Markup.Escape(next)}[/]. It finishes as this command exits.");
            AnsiConsole.MarkupLine("[grey]run [green]tack changelog[/] to see what changed.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]update failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"[grey]feed: {Markup.Escape(repoUrl)} (anonymous GitHub API calls are limited to 60/hour per IP)[/]");
            return 1;
        }
    }

    /// <summary>The GitHub repo URL to update from. <c>TACK_REPO</c> (<c>owner/name</c>, as install.ps1 takes it)
    /// wins, so a fork installed that way keeps updating from the fork; otherwise the build's RepositoryUrl.</summary>
    public static string RepoUrl(string? envRepo, string? metadataUrl)
    {
        if (!string.IsNullOrWhiteSpace(envRepo))
            return $"https://github.com/{envRepo.Trim().Trim('/')}";
        return string.IsNullOrWhiteSpace(metadataUrl) ? "https://github.com/ArcticGizmo/tack" : metadataUrl;
    }

    // Stamped from Directory.Build.props' RepositoryUrl by Tack.Cli.csproj, so the owner lives in one place.
    private static string? MetadataRepoUrl() =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;
}
