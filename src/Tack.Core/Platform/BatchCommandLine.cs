using System.Text;

namespace Tack.Core.Platform;

/// <summary>
/// Builds the cmd.exe arguments that run a <c>.cmd</c>/<c>.bat</c> target (npm.cmd, npx.cmd...) with forwarded
/// arguments that reach the script literally.
///
/// <para>A batch file can only run through cmd.exe, and cmd.exe re-parses its command line: <c>&amp;</c>, <c>|</c>,
/// <c>&lt;</c>, <c>&gt;</c> and <c>^</c> are operators outside quotes, <c>%VAR%</c> expands anywhere, and the quotes
/// after <c>/c</c> are stripped by rules of their own. Ordinary exe quoting (what ProcessStartInfo.ArgumentList
/// does) is therefore not enough: an argument like <c>x&amp;calc</c> runs a second command. This is BatBadBut
/// (CVE-2024-24576). Language runtimes now escape for cmd - or refuse - when they can see they're starting a
/// batch file, but a caller starting a shim sees <c>npm.exe</c>, so the shim has to do it.</para>
///
/// <para>A port of Rust std's fix (<c>make_bat_command_line</c> / <c>append_bat_arg</c>):</para>
/// <list type="bullet">
/// <item>the whole command after <c>/c</c> is wrapped in one extra pair of quotes, which cmd strips, so the target
/// path and every argument keep their own;</item>
/// <item>an argument with anything but letters, digits and <c>#$*+-./:?@\_</c> is quoted, which puts cmd's
/// operators inside quotes where they're literal; an embedded <c>"</c> is doubled, keeping cmd's quote tracking
/// in step;</item>
/// <item>each <c>%</c> becomes <c>%%cd:~,%</c>, an empty substring of <c>%cd%</c> that stops cmd pairing it into
/// a variable (needs <c>/e:ON</c>);</item>
/// <item><c>/v:OFF</c> keeps <c>!</c> literal, and <c>/d</c> skips the AutoRun commands in the registry;</item>
/// <item>line breaks and NULs can't be passed through cmd at all, so an argument holding one is refused.</item>
/// </list>
/// </summary>
public static class BatchCommandLine
{
    // ASCII that is safe unquoted; everything else ASCII (and any control character) forces quotes.
    private const string SafeUnquoted = @"#$*+-./:?@\_";

    /// <summary>The arguments to give cmd.exe to run <paramref name="script"/> with <paramref name="args"/>, or null
    /// with <paramref name="error"/> set when that can't be done safely.</summary>
    public static string? Build(string script, IReadOnlyList<string> args, out string? error)
    {
        // Windows file names can't contain '"', and a trailing '\' would escape the closing quote.
        if (script.Contains('"') || script.EndsWith('\\'))
        {
            error = $"'{script}' can't be run through cmd.exe safely";
            return null;
        }

        var sb = new StringBuilder("/e:ON /v:OFF /d /c \"\"").Append(script).Append('"');
        foreach (var arg in args)
        {
            if (arg.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
            {
                error = "an argument contains a line break or NUL, which can't be passed to a .cmd/.bat file safely";
                return null;
            }
            sb.Append(' ');
            AppendArg(sb, arg);
        }
        sb.Append('"');

        error = null;
        return sb.ToString();
    }

    private static void AppendArg(StringBuilder sb, string arg)
    {
        // Empty must be quoted to survive at all; a trailing '\' is quoted so a script's "%~1" can't have it
        // escape the closing quote.
        bool quote = arg.Length == 0 || arg[^1] == '\\';
        foreach (char c in arg)
            if ((char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !SafeUnquoted.Contains(c)) || char.IsControl(c))
                quote = true;

        if (quote) sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                {
                    // Backslashes before a quote are doubled, and the quote itself is doubled.
                    sb.Append('\\', backslashes).Append('"');
                }
                else if (c == '%')
                {
                    sb.Append("%%cd:~,");
                }
                backslashes = 0;
            }
            sb.Append(c);
        }
        if (quote) sb.Append('\\', backslashes).Append('"');
    }
}
