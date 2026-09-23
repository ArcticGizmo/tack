// tack-stub -- a fake tool the shim can dispatch to (test harness).
//
// Prints machine-readable lines the tests parse, then exits with a chosen code:
//   TOOL=<own exe basename>          which binary actually ran (proves correct dispatch / per-dir selection)
//   CWD=<current directory>          proves the child inherited the caller's working directory
//   ARGS=<a|b|c>                     proves args were forwarded verbatim (order preserved)
//   STDIN=<first line of stdin>      proves std handles were inherited (only if piped)
// Exit code comes from --stub-exit=<n> or env STUB_EXIT (default 0), so exit-code propagation is testable.

string self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "tack-stub");

int exit = 0;
var forwarded = new List<string>();
foreach (var a in args)
{
    if (a.StartsWith("--stub-exit=", StringComparison.Ordinal) && int.TryParse(a["--stub-exit=".Length..], out var n))
        exit = n;
    else
        forwarded.Add(a);
}
if (Environment.GetEnvironmentVariable("STUB_EXIT") is { Length: > 0 } e && int.TryParse(e, out var ee))
    exit = ee;

Console.Out.WriteLine($"TOOL={self}");
Console.Out.WriteLine($"CWD={Environment.CurrentDirectory}");
Console.Out.WriteLine($"ARGS={string.Join('|', forwarded)}");

if (Console.IsInputRedirected)
{
    string? line = Console.In.ReadLine();
    if (line is not null) Console.Out.WriteLine($"STDIN={line}");
}

Console.Error.WriteLine($"STUBERR tool={self}");
return exit;
