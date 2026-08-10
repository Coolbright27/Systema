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

    /// <summary>Fired when the user toggles the Task Sleep engine from the tray menu.</summary>
    public event Action? ToggleTaskSleepRequested;

    /// <summary>Fired when the user picks "Check for updates" from the tray menu.</summary>
    public event Action? CheckForUpdatesRequested;

    /// <summary>Fired with "balanced" / "performance" from the Power plan submenu.</summary>
    public event Action<string>? PowerPlanRequested;

    /// <summary>
    /// Fired just before the menu is shown, so the caller can refresh the toggle states.
    /// Doing it here rather than on a timer means no background work for a menu nobody has opened.
    /// </summary>
    public event Action? MenuOpening;

    // Task Sleep item — caption/checkmark refreshed by UpdateTaskSleepMenuState().
    private ToolStripMenuItem? _sleepItem;

    // ── Dark menu theming ──────────────────────────────────────────────────────
    // The tray menu is WinForms, so it renders in the OS light style by default and
    // clashes with the app. Two things are needed to make it read as ours: a colour
    // table (the stock one paints a light IMAGE MARGIN gutter down the left edge,
    // which is the giveaway even after the background is darkened) and a renderer
    // that forces text/arrow colours, since ProfessionalRenderer would otherwise use
    // system colours for disabled items.
    private sealed class SystemaMenuColors : ProfessionalColorTable
    {
        private static readonly System.Drawing.Color Card     = System.Drawing.Color.FromArgb(0x1E, 0x22, 0x27);
        private static readonly System.Drawing.Color Border   = System.Drawing.Color.FromArgb(0x2E, 0x34, 0x3B);
        private static readonly System.Drawing.Color Hover    = System.Drawing.Color.FromArgb(0x2A, 0x31, 0x3A);

        public override System.Drawing.Color ToolStripDropDownBackground        => Card;
        public override System.Drawing.Color MenuBorder                         => Border;
        public override System.Drawing.Color MenuItemBorder                     => Hover;
        public override System.Drawing.Color MenuItemSelected                   => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin      => Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd        => Hover;
        public override System.Drawing.Color MenuItemPressedGradientBegin       => Hover;
        public override System.Drawing.Color MenuItemPressedGradientMiddle      => Hover;
        public override System.Drawing.Color MenuItemPressedGradientEnd         => Hover;
        // Kill the light left gutter.
        public override System.Drawing.Color ImageMarginGradientBegin           => Card;
        public override System.Drawing.Color ImageMarginGradientMiddle          => Card;
        public override System.Drawing.Color ImageMarginGradientEnd             => Card;
        public override System.Drawing.Color ImageMarginRevealedGradientBegin   => Card;
        public override System.Drawing.Color ImageMarginRevealedGradientMiddle  => Card;
        public override System.Drawing.Color ImageMarginRevealedGradientEnd     => Card;
        public override System.Drawing.Color SeparatorDark                      => Border;
        public override System.Drawing.Color SeparatorLight                     => Border;
        public override System.Drawing.Color CheckBackground                    => System.Drawing.Color.FromArgb(0x24, 0x3A, 0x47);
        public override System.Drawing.Color CheckSelectedBackground            => System.Drawing.Color.FromArgb(0x2B, 0x45, 0x55);
        public override System.Drawing.Color CheckPressedBackground             => System.Drawing.Color.FromArgb(0x2B, 0x45, 0x55);
        public override System.Drawing.Color ToolStripBorder                    => Border;
    }

    private sealed class SystemaMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly System.Drawing.Color TextPrimary = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF7);
        private static readonly System.Drawing.Color TextDim     = System.Drawing.Color.FromArgb(0x88, 0x91, 0x9C);
        private static readonly System.Drawing.Color Accent      = System.Drawing.Color.FromArgb(0x38, 0xBD, 0xF8);

        public SystemaMenuRenderer() : base(new SystemaMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Disabled items (Exit stays enabled; this covers any future dimmed entry) and
            // the right-aligned status hints both read as secondary text.
            e.TextColor = e.Item.Enabled ? TextPrimary : TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = TextDim;          // submenu chevron
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Tint the checkmark to the single accent instead of the system blue.
            using var pen = new Pen(Accent, 1.8f);
            var r = e.ImageRectangle;
            int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(cx - 4f, cy),
                new PointF(cx - 1f, cy + 3.2f),
                new PointF(cx + 4.5f, cy - 3.6f),
            });
        }
    }

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

        StartVisibilityReassert();
    }

    // When Systema launches at boot it can beat explorer's taskbar to the punch, so the icon's
    // initial add silently fails. WinForms re-adds on the TaskbarCreated broadcast, but if the
    // shell was already up (just not tray-ready yet) that broadcast never comes and the icon stays
    // missing — which locks a tray-only app out of view. Re-asserting the icon a few times over the
    // first ~20 s forces a re-add once the tray is ready. Each toggle is a brief no-op flicker if the
    // icon is already present.
    private System.Windows.Forms.Timer? _reassertTimer;
    private int _reassertTicks;

    private void StartVisibilityReassert()
    {
        _reassertTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _reassertTimer.Tick += (_, _) =>
        {
            try { _notifyIcon.Visible = false; _notifyIcon.Visible = true; } catch { /* non-critical */ }
            if (++_reassertTicks >= 4)
            {
                _reassertTimer?.Stop();
                _reassertTimer?.Dispose();
                _reassertTimer = null;
            }
        };
        _reassertTimer.Start();
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

    // ── Temporary priority borrow ─────────────────────────────────────────────
    // Ghost Mode parks the process at IDLE_PRIORITY_CLASS, which is right for sitting in the
    // tray doing nothing and wrong for the one moment that matters: applying a game boost.
    // A boost fires exactly when the machine is busiest (a game is launching), so at Idle
    // priority its registry reads, power-plan switch and Dell BIOS WMI calls stretched from
    // well under a second to nine, and the UI heartbeat starved long enough for the crash
    // watchdog to file a false "UI THREAD FREEZE" report.
    //
    // Callers borrow Normal priority for the duration of that work and give it straight back.
    // Refcounted, because an activate and a deactivate can overlap.
    private int _priorityBorrows;

    /// <summary>
    /// Runs the caller at Normal priority even while Ghost Mode is active, restoring Idle when
    /// the returned handle is disposed. Returns null when there is nothing to restore.
    /// </summary>
    public IDisposable? BorrowNormalPriority()
    {
        if (!_isGhostMode) return null;

        try
        {
            if (Interlocked.Increment(ref _priorityBorrows) == 1)
                SetPriorityClass(GetCurrentProcess(), NORMAL_PRIORITY_CLASS);
        }
        catch { /* priority is an optimisation, never fail the caller over it */ }

        return new PriorityBorrow(this);
    }

    private void ReturnBorrowedPriority()
    {
        try
        {
            // Only drop back to Idle if Ghost Mode is still on — the window may have been
            // restored while we held the borrow, and that must win.
            if (Interlocked.Decrement(ref _priorityBorrows) == 0 && _isGhostMode)
                SetPriorityClass(GetCurrentProcess(), IDLE_PRIORITY_CLASS);
        }
        catch { }
    }

    private sealed class PriorityBorrow : IDisposable
    {
        private TrayService? _owner;
        public PriorityBorrow(TrayService owner) => _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.ReturnBorrowedPriority();
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
        var menu = new ContextMenuStrip
        {
            Renderer     = new SystemaMenuRenderer(),
            BackColor    = System.Drawing.Color.FromArgb(0x1E, 0x22, 0x27),
            ForeColor    = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF7),
            ShowCheckMargin = false,   // the image margin already hosts the checkmark
            ShowImageMargin = true,    // keep it: without it, Checked items draw nothing
        };

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

        // Task Sleep engine. The napped count rides in ShortcutKeyDisplayString, which
        // WinForms right-aligns for free — no custom drawing needed for the status hint.
        _sleepItem = new ToolStripMenuItem("Task Sleep");
        _sleepItem.Click += (_, _) => ToggleTaskSleepRequested?.Invoke();

        var powerItem = new ToolStripMenuItem("Power plan");
        // Only the two plans PowerPlanService actually implements.
        foreach (var (label, key) in new[]
                 {
                     ("Balanced",         "balanced"),
                     ("High performance", "performance"),
                 })
        {
            var planKey = key;
            var sub = new ToolStripMenuItem(label);
            sub.Click += (_, _) => PowerPlanRequested?.Invoke(planKey);
            powerItem.DropDownItems.Add(sub);
        }
        // The submenu is its own ToolStrip, so it needs the renderer too or it opens light.
        powerItem.DropDown.Renderer  = new SystemaMenuRenderer();
        powerItem.DropDown.BackColor = System.Drawing.Color.FromArgb(0x1E, 0x22, 0x27);
        powerItem.DropDown.ForeColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF7);

        var updateItem = new ToolStripMenuItem("Check for updates");
        updateItem.Click += (_, _) => CheckForUpdatesRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Exit Systema");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_boostItem);
        menu.Items.Add(_sleepItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(powerItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) => MenuOpening?.Invoke();
        return menu;
    }

    /// <summary>
    /// Refreshes the Task Sleep row (checkmark + right-aligned on/off hint).
    /// The engine exposes no live "napped count", so the hint states the engine state rather
    /// than inventing a number — a wrong count would be worse than no count.
    /// </summary>
    public void UpdateTaskSleepMenuState(bool engineOn)
    {
        if (_sleepItem == null) return;
        void Apply()
        {
            _sleepItem.Checked = engineOn;
            _sleepItem.ShortcutKeyDisplayString = engineOn ? "on" : "off";
        }
        if (_notifyIcon.ContextMenuStrip is { } cms && cms.InvokeRequired)
            cms.BeginInvoke((Action)Apply);
        else
            Apply();
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
        try { _reassertTimer?.Stop(); _reassertTimer?.Dispose(); } catch { }
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
