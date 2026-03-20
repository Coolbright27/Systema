// ══════════════════════════════════════════════════════════════════════════════
// BloatwareEntry.cs  ·  A single detectable pre-installed app
// ══════════════════════════════════════════════════════════════════════════════

using System.ComponentModel;

namespace Systema.Models;

public class BloatwareEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>Friendly display name shown in the UI.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Plain-English description of what the app does and why it is safe to remove.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// For UWP apps: the AppxPackage name that matched.
    /// For Win32 apps: the registry DisplayName that matched (used for logging).
    /// </summary>
    public string PackageName { get; init; } = string.Empty;

    /// <summary>True if this is a Win32 app (detected via registry uninstall keys).</summary>
    public bool IsWin32 { get; init; }

    /// <summary>Winget package ID used to uninstall Win32 apps (e.g. Dell.SupportAssist).</summary>
    public string? WingetId { get; init; }

    /// <summary>
    /// UninstallString from the registry — used as a fallback when winget is unavailable or fails.
    /// </summary>
    public string? UninstallString { get; init; }

    /// <summary>True once the scanner confirms this package is installed on this machine.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Whether the user has ticked this entry for removal.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
