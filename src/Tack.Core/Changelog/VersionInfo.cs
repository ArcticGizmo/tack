using System.Reflection;

namespace Tack.Core.Changelog;

/// <summary>
/// Reads an assembly's product version - the single value that <c>Directory.Build.props</c> stamps as the
/// source of truth and that the changelog "what's new" logic diffs against. Strips the <c>+sha</c> build
/// suffix so it compares cleanly with the <c>vX.Y.Z</c> headings in <c>CHANGELOG.md</c>.
/// </summary>
public static class VersionInfo
{
    /// <summary>The clean <c>X.Y.Z</c> version of <paramref name="assembly"/> (defaults to the caller's).</summary>
    public static string Of(Assembly? assembly = null)
    {
        var asm = assembly ?? Assembly.GetCallingAssembly();
        string v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? asm.GetName().Version?.ToString()
                   ?? "0.0.0";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
