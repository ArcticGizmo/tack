namespace Tack.Core;

/// <summary>
/// Puts tack's shims dir (and install dir) on the user PATH, and takes them off again. Behind an interface
/// so a macOS/Linux implementation (shell-profile equivalent) can be added later without touching callers.
/// </summary>
public interface IPathInstaller
{
    /// <summary>Ensure the shims dir + install dir are on the user PATH. Idempotent.</summary>
    void Register();

    /// <summary>Remove tack's entries from the user PATH. Idempotent.</summary>
    void Unregister();
}
