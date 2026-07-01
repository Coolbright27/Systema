// ════════════════════════════════════════════════════════════════════════════
// TrayService.cs  ·  System tray icon and Ghost Mode (background throttle)
// ════════════════════════════════════════════════════════════════════════════
//
// Creates and manages a WinForms NotifyIcon for the system tray. When the main
// window is hidden, Ghost Mode lowers the app's own process priority and trims
// its working set via P/Invoke to minimise resource impact while running in the
// background. Ghost Mode is cancelled when the window is shown again.
//
// RELATED FILES
//   App.xaml.cs          — creates TrayService and passes window visibility events
//   GameBoosterService.cs — may also signal Ghost Mode during active game boost
// ════════════════════════════════════════════════════════════════════════════

using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Systema.Services;

/// <summary>
/// Manages the system-tray NotifyIcon and Ghost Mode (low-priority idle state).
/// Ghost Mode: working set trimmed, process priority set to Idle, background scans run slow.
/// </summary>
public sealed class TrayService : IDisposable
{
    // ── P/Invoke ───────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    // EmptyWorkingSet lives in psapi.dll (not kernel32.dll).
    // On Windows 8+ kernel32 re-exports it as K32EmptyWorkingSet, but the
    // undecorated name is only guaranteed in psapi.dll across all Win10 builds.
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const uint IDLE_PRIORITY_CLASS       = 0x0040;
    private const uint NORMAL_PRIORITY_CLASS     = 0x0020;

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly NotifyIcon _notifyIcon;
    private static readonly LoggerService _log = LoggerService.Instance;

    private bool _isGhostMode;
    public bool IsGhostMode => _isGhostMode;

    // The "Toggle Game Boost" menu item — kept as a field so its caption and
    // checkmark can be refreshed by UpdateBoostMenuState() when the boost state
    // changes (whether triggered from the tray, the UI, or game auto-detection).
    private ToolStripMenuItem? _boostItem;

    /// <summary>Fired when the user requests to show the main window from the tray menu.</summary>
    public event Action? ShowWindowRequested;

    /// <summary>Fired when the user requests to exit from the tray menu.</summary>
    public event Action? ExitRequested;

    /// <summary>
    /// Fired when the user clicks "Toggle Game Boost" in the tray menu.
    /// App.xaml.cs wires this to GameBoosterService.Enable/DisableManualBoostAsync.
    /// </summary>
    public event Action? ToggleBoostRequested;

    // ── Constructor ────────────────────────────────────────────────────────────
    public TrayService()
    {
        _notifyIcon = new NotifyIcon
        {
            Text    = "Systema — Windows Optimizer",
            Visible = true,
            Icon    = LoadIcon()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowWindowRequested?.Invoke();
        _notifyIcon.ContextMenuStrip = BuildContextMenu();
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Enter Ghost Mode: trim RAM, drop to Idle CPU priority.</summary>
    public void EnterGhostMode()
    {
        if (_isGhostMode) return;
        _isGhostMode = true;

        try
        {
            var hProcess = GetCurrentProcess();
            SetPriorityClass(hProcess, IDLE_PRIORITY_CLASS);
            EmptyWorkingSet(hProcess);
            _log.Info("TrayService", "Ghost Mode activated (Idle priority, working set trimmed)");
        }
        catch (Exception ex)
        {
            _log.Warn("TrayService", "Failed to enter Ghost Mode fully", ex);
        }
    }

    /// <summary>Exit Ghost Mode: restore Normal CPU priority.</summary>
    public void ExitGhostMode()
    {
        if (!_isGhostMode) return;
        _isGhostMode = false;

        try
        {
            var hProcess = GetCurrentProcess();
            SetPriorityClass(hProcess, NORMAL_PRIORITY_CLASS);
            _log.Info("TrayService", "Ghost Mode deactivated (Normal priority restored)");
        }
        catch (Exception ex)
        {
            _log.Warn("TrayService", "Failed to restore Normal priority", ex);
        }
    }

    /// <summary>Show a balloon tip notification from the tray icon.</summary>
    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info, int timeout = 4000)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(timeout, title, message, icon);
        }
        catch { /* non-critical */ }
    }

    /// <summary>Update the tray icon tooltip text (e.g. when Game Boost is active).</summary>
    public void SetTooltip(string text)
    {
        // NotifyIcon.Text has a 63-char limit
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>
    /// Refreshes the "Toggle Game Boost" tray menu item to reflect the current
    /// boost state. Call from App.xaml.cs on BoostActivated / BoostDeactivated so
    /// the menu caption + checkmark stay in sync no matter what triggered the
    /// change (tray click, in-app toggle, or game auto-detection).
    /// Safe to call from any thread — marshals onto the NotifyIcon's owning thread.
    /// </summary>
    public void UpdateBoostMenuState(bool boostActive)
    {
        if (_boostItem == null) return;
        void Apply()
        {
            _boostItem.Text    = boostActive ? "Stop Game Boost" : "Start Game Boost";
            _boostItem.Checked = boostActive;
        }
        // ContextMenuStrip is a WinForms control — touch it on its own thread.
        if (_notifyIcon.ContextMenuStrip is { } cms && cms.InvokeRequired)
            cms.BeginInvoke((Action)Apply);
        else
            Apply();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Open Systema");
        // Create a bold font and track it explicitly — WinForms does not dispose fonts set on menu items,
        // so we hook the menu's Disposed event to release the GDI resource.
        var boldFont = new Font(openItem.Font, openItem.Font.Style | System.Drawing.FontStyle.Bold);
        openItem.Font = boldFont;
        menu.Disposed += (_, _) => boldFont.Dispose();
        openItem.Click += (_, _) => ShowWindowRequested?.Invoke();

        // "Toggle Game Boost" — lets the user start/stop Game Boost without opening
        // the window. Caption + checkmark are refreshed by UpdateBoostMenuState().
        _boostItem = new ToolStripMenuItem("Start Game Boost");
        _boostItem.Click += (_, _) => ToggleBoostRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_boostItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private static Icon LoadIcon()
    {
        // Primary: the icon embedded in this assembly. It always matches the current build and has no
        // file-path dependency. (The single-file deployment doesn't place a loose logo.ico next to the
        // exe, and an old copy can linger in {app}\Assets — reading that file showed a stale tray icon.)
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("logo.ico", StringComparison.OrdinalIgnoreCase));
            if (resName != null)
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
        }
        catch { /* fall through to the on-disk copy */ }

        // Fallback: a logo.ico deployed next to the app.
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath);
        }
        catch { /* fall through to system icon */ }

        // Last resort: a standard system icon.
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
