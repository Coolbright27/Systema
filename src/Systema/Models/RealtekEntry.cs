namespace Systema.Models;

/// <summary>
/// Represents a Realtek bloatware entry discovered in the Windows uninstall registry.
/// Only non-driver entries (Manager/Console apps) are surfaced here.
/// </summary>
public class RealtekEntry
{
    public string DisplayName     { get; set; } = string.Empty;
    public string Version         { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;

    /// <summary>
    /// Preferred quiet uninstall string (may be empty; fall back to UninstallString).
    /// </summary>
    public string QuietUninstallString { get; set; } = string.Empty;

    /// <summary>
    /// Registry key path this entry was read from (for diagnostics).
    /// </summary>
    public string RegistryKey { get; set; } = string.Empty;
}
