// M10 checkpoint 0 spike: what do the vendor archives look like once unpacked with the API tack will use?
// Throwaway. Findings live in docs/m10-spike-findings.md.
using System.IO.Compression;

string root = AppContext.GetData("SpikeRoot") as string ?? Directory.GetCurrentDirectory();
string downloads = Path.Combine(root, "downloads");
string unpacked = Path.Combine(root, "unpacked");

ZipSlip();

foreach (var zip in Directory.GetFiles(downloads, "*.zip").Order())
{
    using var archive = ZipFile.OpenRead(zip);
    var tops = archive.Entries
        .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    var longest = archive.Entries.MaxBy(e => e.FullName.Length)!;
    Console.WriteLine($"== {Path.GetFileName(zip)}");
    Console.WriteLine($"   entries {archive.Entries.Count}, top-level names {tops.Count}: {string.Join(", ", tops.Take(6))}{(tops.Count > 6 ? ", ..." : "")}");
    Console.WriteLine($"   longest entry {longest.FullName.Length} chars: {longest.FullName}");

    string dest = Path.Combine(unpacked, Path.GetFileNameWithoutExtension(zip));
    if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    ZipFile.ExtractToDirectory(zip, dest);
    sw.Stop();
    int longestOnDisk = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Max(f => f.Length);
    Console.WriteLine($"   extracted in {sw.ElapsedMilliseconds} ms to {dest}");
    Console.WriteLine($"   longest path on disk {longestOnDisk} chars (MAX_PATH is 260)");

    // What a version would expose: exec files in the top folder and in Scripts\.
    string home = tops.Count == 1 ? Path.Combine(dest, tops[0]) : dest;
    foreach (var dir in new[] { home, Path.Combine(home, "Scripts") }.Where(Directory.Exists))
    {
        var execs = Directory.GetFiles(dir).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".cmd" or ".bat")
            .Select(Path.GetFileName);
        Console.WriteLine($"   execs in {Path.GetRelativePath(dest, dir)}: {string.Join(" ", execs)}");
    }
    foreach (var probe in new[] { "python._pth", "python312._pth", "python314._pth", "Lib/ensurepip", "Lib/site-packages/pip", "DLLs/_tkinter.pyd", "node_modules/corepack", "node_modules/npm/npmrc" })
        if (File.Exists(Path.Combine(home, probe)) || Directory.Exists(Path.Combine(home, probe)))
            Console.WriteLine($"   has {probe}");
}

// Does ExtractToDirectory refuse an entry that climbs out of the destination?
static void ZipSlip()
{
    string tmp = Path.Combine(Path.GetTempPath(), "m10-zipslip-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    string zip = Path.Combine(tmp, "evil.zip");
    using (var a = ZipFile.Open(zip, ZipArchiveMode.Create))
    using (var w = new StreamWriter(a.CreateEntry("../escaped.txt").Open()))
        w.Write("x");
    try
    {
        ZipFile.ExtractToDirectory(zip, Path.Combine(tmp, "out"));
        Console.WriteLine($"zip slip: NOT refused (escaped file exists: {File.Exists(Path.Combine(tmp, "escaped.txt"))})");
    }
    catch (IOException e) { Console.WriteLine($"zip slip: refused - {e.Message}"); }
    finally { Directory.Delete(tmp, recursive: true); }
}
