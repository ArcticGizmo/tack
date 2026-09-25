using Tack.Core.Platform;

namespace Tack.Core;

/// <summary>
/// Puts tack's shims dir (and install dir) on the system PATH, and takes them off again. Never the user PATH.
/// Behind an interface so a macOS/Linux implementation (shell-profile equivalent) can be added later without
/// touching callers.
/// </summary>
public interface IPathInstaller
{
    /// <summary>Ensure the shims dir + install dir are on the system PATH. Idempotent; null if nothing changed.</summary>
    PathChange? Register();

    /// <summary>Remove tack's entries from the system PATH. Idempotent; null if nothing changed.</summary>
    PathChange? Unregister();
}
