using System.Diagnostics;
using System.Text;

namespace Tack.Core.Installs;

/// <summary>Runs a version's post-install commands. Injected, so the engine is tested without running anything.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancel);
}

/// <summary>The exit code, and stdout and stderr together, in the order they arrived.</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>The real <see cref="IProcessRunner"/>: no shell, no window, output captured; cancelling kills the process tree.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string workingDirectory, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InstallException($"couldn't run {exe}: {e.Message}", e);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        process.WaitForExit(); // flush the async output readers
        lock (output) return new ProcessResult(process.ExitCode, output.ToString());
    }
}
