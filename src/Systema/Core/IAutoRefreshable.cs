// ════════════════════════════════════════════════════════════════════════════
// IAutoRefreshable.cs  ·  Contract for ViewModels that support periodic refresh
// ════════════════════════════════════════════════════════════════════════════
//
// Single-method interface requiring a RefreshAsync() Task. MainViewModel holds a
// DispatcherTimer and calls RefreshAsync() on whichever VM is currently active:
// every 1 second when the window is focused, every 5 seconds when unfocused.
//
// RELATED FILES
//   MainViewModel.cs      — drives the refresh timer and casts active VM to this
//   ProcessViewModel.cs   — implements IAutoRefreshable
//   MemoryViewModel.cs    — implements IAutoRefreshable
//   ServicesViewModel.cs  — implements IAutoRefreshable
//   (other VMs that implement it)
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

/// <summary>
/// Implemented by ViewModels that support periodic auto-refresh.
/// MainViewModel calls RefreshAsync() on the active VM at 1s (focused) / 5s (unfocused).
/// </summary>
public interface IAutoRefreshable
{
    /// <summary>
    /// Reload all data for this view. Must be idempotent and guard against concurrent calls.
    /// </summary>
    Task RefreshAsync();
}
