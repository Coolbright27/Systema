// ════════════════════════════════════════════════════════════════════════════
// BloatwareEntry.cs  ·  A single detectable pre-installed app
// ════════════════════════════════════════════════════════════════════════════
//
// Represents one pre-installed Microsoft app that Systema can safely remove.
// IsSelected is INotifyPropertyChanged so a CheckBox can bind TwoWay without
// the ViewModel having to refresh the whole collection on every tick.
//
// RELATED FILES
//   BloatwareService.cs    — catalogue definition + scan/remove logic
//   BloatwareViewModel.cs  — drives the Bloatware tab
// ════════════════════════════════════════════════════════════════════════════

using System.ComponentModel;

namespace Systema.Models;

public class BloatwareEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>Friendly display name shown in the UI.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Plain-English description of what the app does and why it is safe to remove.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Primary AppxPackage name (may be one of several checked).</summary>
    public string PackageName { get; init; } = string.Empty;

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
