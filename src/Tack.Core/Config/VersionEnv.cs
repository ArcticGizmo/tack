namespace Tack.Core.Config;

/// <summary>
/// Environment variables a registered version sets for its own process, e.g.
/// <c>tack tool add claude@work --env CLAUDE_CONFIG_DIR=%USERPROFILE%\.claude-work</c>. Values may hold
/// <c>%VARS%</c>, expanded by the shim at call time; an empty value (<c>NAME=</c>) unsets the variable instead.
///
/// <para>They live only in central config. A repo's tack.yml can pick a version by name but can never set a
/// variable, so cloning a repo can't smuggle <c>NODE_OPTIONS</c> into every tool call made inside it.</para>
/// </summary>
public static class VersionEnv
{
    /// <summary>Parse one <c>NAME=VALUE</c>. The name is everything before the first <c>=</c>; the value is kept
    /// exactly as given (it may itself contain <c>=</c>).</summary>
    public static bool TryParse(string spec, out string name, out string value, out string? error)
    {
        name = value = "";
        int eq = spec.IndexOf('=');
        if (eq <= 0)
        {
            error = $"'{spec}' is not NAME=VALUE";
            return false;
        }
        name = spec[..eq];
        value = spec[(eq + 1)..];
        if (name.Any(char.IsWhiteSpace))
        {
            error = $"'{name}' is not a usable variable name (it has whitespace in it)";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Parse several <c>NAME=VALUE</c>s into a map (a later duplicate wins). Null when there are none, or
    /// on the first bad one with <paramref name="error"/> set.</summary>
    public static Dictionary<string, string>? Parse(IEnumerable<string>? specs, out string? error)
    {
        error = null;
        Dictionary<string, string>? env = null;
        foreach (var spec in specs ?? Array.Empty<string>())
        {
            if (!TryParse(spec, out var name, out var value, out error)) return null;
            env ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            env[name] = value;
        }
        return env;
    }

    /// <summary>Apply a version's variables to a child's environment block: set each one (after
    /// <paramref name="expand"/>), or remove it when its value is empty.</summary>
    public static void ApplyTo(IDictionary<string, string?> target, IReadOnlyDictionary<string, string> env,
        Func<string, string> expand)
    {
        foreach (var (name, value) in env)
        {
            if (value.Length == 0) target.Remove(name);
            else target[name] = expand(value);
        }
    }
}
