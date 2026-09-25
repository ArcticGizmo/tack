using System.Text.Json;
using Tack.Core.Config;
using Tack.Core.Diagnostics;
using Xunit;

namespace Tack.Tests;

public sealed class ShimLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tack-log-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Log_lives_beside_resolved_json()
    {
        Assert.Equal(Path.Combine(_root, "logs", "shim.log"), ShimLog.PathFor(Path.Combine(_root, "resolved.json")));
    }

    [Fact]
    public void Format_lists_the_chain_parent_first_and_says_why_it_stopped()
    {
        string text = ShimLog.Format(new ShimLogEntry
        {
            Time = new DateTime(2026, 9, 25, 14, 3, 12, 481),
            Pid = 100,
            Exposed = "node",
            Args = ["--version", "a b", ""],
            Cwd = @"C:\repo",
            Source = "TackYml",
            Version = "20.11.0",
            Detail = @"C:\repo\tack.yml",
            Target = @"C:\node\20\node.exe",
            Callers = [new(200, @"C:\Windows\System32\cmd.exe"), new(300, null)],
            CallersEnd = "(parent [400] has exited)",
        });

        Assert.Equal(
            "2026-09-25 14:03:12.481  node 20.11.0  (TackYml: C:\\repo\\tack.yml)  pid 100\n" +
            "  args    --version \"a b\" \"\"\n" +
            "  cwd     C:\\repo\n" +
            "  runs    C:\\node\\20\\node.exe\n" +
            "  caller  [200] C:\\Windows\\System32\\cmd.exe\n" +
            "          [300] (image path unavailable)\n" +
            "          (parent [400] has exited)\n" +
            "\n",
            text);
    }

    [Fact]
    public void Append_adds_entries_and_rolls_over_when_full()
    {
        string log = Path.Combine(_root, "logs", "shim.log");
        Assert.True(ShimLog.Append(log, "one\n"));
        Assert.True(ShimLog.Append(log, "two\n"));
        Assert.Equal("one\ntwo\n", File.ReadAllText(log));

        File.WriteAllText(log, new string('x', (int)ShimLog.MaxBytes));
        Assert.True(ShimLog.Append(log, "fresh\n"));
        Assert.Equal("fresh\n", File.ReadAllText(log));
        Assert.Equal(ShimLog.MaxBytes, new FileInfo(log + ".1").Length);
    }

    [Fact]
    public void Log_setting_compiles_through_and_stays_out_of_json_when_off()
    {
        var off = JsonSerializer.Serialize(ConfigCompiler.Compile(new CentralConfig()), TackJson.Default.ResolvedConfig);
        Assert.DoesNotContain("\"log\"", off);

        var config = new CentralConfig { Settings = { Log = true } };
        var json = JsonSerializer.Serialize(ConfigCompiler.Compile(config), TackJson.Default.ResolvedConfig);
        Assert.True(JsonSerializer.Deserialize(json, TackJson.Default.ResolvedConfig)!.Settings.Log);
    }
}
