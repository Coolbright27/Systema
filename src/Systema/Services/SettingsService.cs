// ════════════════════════════════════════════════════════════════════════════
// SettingsService.cs  ·  User preferences persisted to HKCU\Software\Systema
// ════════════════════════════════════════════════════════════════════════════
//
// Loads and saves all user-facing preferences (e.g. skip-restore-point flag,
// auto-boost enabled) via the registry. Exposes strongly typed properties;
// callers set a property and call SaveSettings to persist immediately.
//
// QUICK EDIT GUIDE
//   To add a new preference → add a property + read in LoadSettings + write in SaveSettings
//
// RELATED FILES
//   SettingsViewModel.cs    — binds preferences to the Settings tab UI
//   GameBoosterService.cs   — reads AutoBoostEnabled preference
//   GameBoosterViewModel.cs — reads/writes AutoBoostEnabled via this service
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Systema.Services;

/// <summary>
/// Persists user preferences to HKCU\Software\Systema.
/// All reads return safe defaults when the key doesn't exist yet.
/// </summary>
public class SettingsService
{
    private const string RegistryKey = @"Software\Systema";

    // ── Restore Point ─────────────────────────────────────────────────────────

    public bool SkipRestorePoint
    {
        get => ReadBool(nameof(SkipRestorePoint), defaultValue: false);
        set => WriteBool(nameof(SkipRestorePoint), value);
    }

    // ── Game Booster ──────────────────────────────────────────────────────────

    /// <summary>How often (in minutes) to poll for running game processes. Default: 2.</summary>
    public int GameCheckIntervalMinutes
    {
        get => ReadInt(nameof(GameCheckIntervalMinutes), defaultValue: 2);
        set => WriteInt(nameof(GameCheckIntervalMinutes), Math.Max(1, value));
    }

    // GameBoosterKillList was removed along with the rest of the service-pausing feature
    // (dropped 2026-06). Any value left in the registry from an older build is simply ignored.

    /// <summary>When true the user has manually chosen Xbox service state — auto-logic won't override.</summary>
    public bool XboxServicesUserOverride
    {
        get => ReadBool(nameof(XboxServicesUserOverride), defaultValue: false);
        set => WriteBool(nameof(XboxServicesUserOverride), value);
    }

    // ── Core Parking ────────────────────────────────────────────────────────

    /// <summary>Persists whether the user has enabled forced core parking enforcement.</summary>
    public bool CoreParkingEnabled
    {
        get => ReadBool(nameof(CoreParkingEnabled), defaultValue: false);
        set => WriteBool(nameof(CoreParkingEnabled), value);
    }

    // ── Graphics tweaks (re-asserted on launch when the user set them) ─────────
    // A GPU driver update or Windows feature update can silently reset these, so we remember the
    // user's explicit choice and re-apply it on launch only when the live value has drifted off.
    // The on/off mirrors (HAGS, windowed opts) use -1 to mean "no preference / never touched".

    /// <summary>User asked to disable Multi-Plane Overlay. Default false (no preference).</summary>
    public bool GraphicsMpoDisabled
    {
        get => ReadBool(nameof(GraphicsMpoDisabled), defaultValue: false);
        set => WriteBool(nameof(GraphicsMpoDisabled), value);
    }

    /// <summary>User asked to extend the GPU recovery timeout (TdrDelay). Default false.</summary>
    public bool GraphicsTdrExtended
    {
        get => ReadBool(nameof(GraphicsTdrExtended), defaultValue: false);
        set => WriteBool(nameof(GraphicsTdrExtended), value);
    }

    /// <summary>User asked to turn off Game DVR / Game Bar background capture. Default false.</summary>
    public bool GraphicsGameDvrDisabled
    {
        get => ReadBool(nameof(GraphicsGameDvrDisabled), defaultValue: false);
        set => WriteBool(nameof(GraphicsGameDvrDisabled), value);
    }

    /// <summary>HAGS preference: -1 none, 0 off, 1 on. Default -1 (never touched).</summary>
    public int GraphicsHagsPref
    {
        get => ReadInt(nameof(GraphicsHagsPref), defaultValue: -1);
        set => WriteInt(nameof(GraphicsHagsPref), value);
    }

    /// <summary>Windowed-game optimizations preference: -1 none, 0 off, 1 on. Default -1.</summary>
    public int GraphicsWindowedOptPref
    {
        get => ReadInt(nameof(GraphicsWindowedOptPref), defaultValue: -1);
        set => WriteInt(nameof(GraphicsWindowedOptPref), value);
    }

    /// <summary>One-time flag: on first launch after reinforcement shipped we adopt the user's
    /// already-applied disable tweaks (MPO/TdrDelay/Game DVR) as intent so they get reinforced without
    /// a re-toggle. Set true once done.</summary>
    public bool GraphicsIntentSeeded
    {
        get => ReadBool(nameof(GraphicsIntentSeeded), defaultValue: false);
        set => WriteBool(nameof(GraphicsIntentSeeded), value);
    }

    // ── Windows Update tweaks ─────────────────────────────────────────────────

    /// <summary>Persists whether the user has enabled blocking of Windows preview updates.</summary>
    public bool BlockPreviewUpdatesEnabled
    {
        get => ReadBool(nameof(BlockPreviewUpdatesEnabled), defaultValue: false);
        set => WriteBool(nameof(BlockPreviewUpdatesEnabled), value);
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private const string TaskName    = "Systema";
    private const string RunKey       = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Systema";

    /// <summary>
    /// Gets or sets whether Systema launches automatically at Windows startup.
    /// Uses a Task Scheduler logon task with "Run with highest privileges" so
    /// the admin app starts silently without a UAC prompt. Falls back to the
    /// HKCU Run key if Task Scheduler is unavailable.
    /// </summary>
    public bool StartWithWindows
    {
        get
        {
            // Primary: Task Scheduler task
            try
            {
                using var ts   = new TaskService();
                var task = ts.GetTask(TaskName);
                if (task != null) return task.Enabled;
            }
            catch { }

            // Fallback: HKCU Run key
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValueName) != null;
            }
            catch { return false; }
        }
        set
        {
            // Try Task Scheduler first (required for admin apps to start without UAC)
            try
            {
                using var ts = new TaskService();

                if (!value)
                {
                    ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
                    // Also remove any legacy Run key entry
                    try
                    {
                        using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                        runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                    }
                    catch { }
                    return;
                }

                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                var td = ts.NewTask();
                td.RegistrationInfo.Description = "Starts Systema optimization suite at logon";
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries     = false;
                td.Settings.RunOnlyIfNetworkAvailable  = false;
                td.Settings.ExecutionTimeLimit         = TimeSpan.Zero; // never time out
                td.Settings.MultipleInstances          = TaskInstancesPolicy.IgnoreNew;

                // Run with highest privileges — this is what lets an admin app start
                // at logon without triggering a UAC elevation prompt.
                td.Principal.RunLevel  = TaskRunLevel.Highest;
                td.Principal.LogonType = TaskLogonType.InteractiveToken;

                // Trigger: when the current user logs on
                string currentUser = WindowsIdentity.GetCurrent().Name;
                td.Triggers.Add(new LogonTrigger { UserId = currentUser });

                // Action: launch the installed EXE in silent/tray mode
                string workDir = Path.GetDirectoryName(exePath) ?? "";
                td.Actions.Add(new ExecAction(exePath, "--autostart", workDir));

                ts.RootFolder.RegisterTaskDefinition(
                    TaskName, td,
                    TaskCreation.CreateOrUpdate,
                    null, null,
                    TaskLogonType.InteractiveToken);

                // Remove any legacy Run key entry to avoid double-launch
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                    runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
                catch { }
                return;
            }
            catch { /* fall through to Run key */ }

            // Fallback: HKCU Run key (non-admin builds / Task Scheduler unavailable)
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return;
                if (value)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                        key.SetValue(RunValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                }
            }
            catch { }
        }
    }

    // ── Battery Optimization ──────────────────────────────────────────────────

    /// <summary>
    /// Persists the active battery optimization mode across sessions.
    /// "" = none, "balanced" = 99% DC cap, "max" = 80% DC cap.
    /// </summary>
    public string BatteryOptimizationMode
    {
        get => ReadString(nameof(BatteryOptimizationMode), defaultValue: "") ?? "";
        set
        {
            // Guard against corrupt/unknown values — only "" | "balanced" | "max" are valid.
            if (value != "" && value != "balanced" && value != "max") value = "";
            WriteString(nameof(BatteryOptimizationMode), value);
        }
    }

    /// <summary>
    /// Dell BIOS thermal profile to apply while on AC power (or on a desktop).
    /// Empty = Systema doesn't manage the thermal profile. Value is the raw BIOS
    /// enum value (e.g. "Optimized", "UltraPerformance"), validated against the
    /// machine's actual PossibleValues by ThermalManagementService before use.
    /// </summary>
    public string ThermalModeAc
    {
        get => ReadString(nameof(ThermalModeAc), defaultValue: "") ?? "";
        set => WriteString(nameof(ThermalModeAc), value);
    }

    /// <summary>
    /// Dell BIOS thermal profile to apply while on battery. Empty = unmanaged.
    /// Only meaningful on laptops; ignored on desktops.
    /// </summary>
    public string ThermalModeBattery
    {
        get => ReadString(nameof(ThermalModeBattery), defaultValue: "") ?? "";
        set => WriteString(nameof(ThermalModeBattery), value);
    }

    /// <summary>
    /// User explicitly toggled High Performance Mode on. When true, Systema restores
    /// High Performance every time the user plugs back in after running on battery.
    /// </summary>
    public bool PerformanceModeEnabled
    {
        get => ReadBool(nameof(PerformanceModeEnabled), defaultValue: false);
        set => WriteBool(nameof(PerformanceModeEnabled), value);
    }

    /// <summary>
    /// The power plan that was active before battery optimization was enabled.
    /// Persisted so a reboot or hibernate-resume restores the correct plan on plug-in.
    /// </summary>
    public string PlanBeforeOptimization
    {
        get => ReadString(nameof(PlanBeforeOptimization), defaultValue: "") ?? "";
        set => WriteString(nameof(PlanBeforeOptimization), value);
    }

    // ── Game Booster — per-session actions ────────────────────────────────────

    /// <summary>Free RAM from background processes when a game starts.</summary>
    public bool GameBoosterFreeMemory
    {
        get => ReadBool(nameof(GameBoosterFreeMemory), defaultValue: true);
        set => WriteBool(nameof(GameBoosterFreeMemory), value);
    }

    /// <summary>Enable Focus Assist (suppress notifications) during gaming.</summary>
    public bool GameBoosterSuppressNotifications
    {
        get => ReadBool(nameof(GameBoosterSuppressNotifications), defaultValue: true);
        set => WriteBool(nameof(GameBoosterSuppressNotifications), value);
    }

    /// <summary>Master switch — when false the game booster never activates.</summary>
    public bool GameBoosterEnabled
    {
        get => ReadBool(nameof(GameBoosterEnabled), defaultValue: true);
        set => WriteBool(nameof(GameBoosterEnabled), value);
    }

    /// <summary>Disable Game Bar while a game is active (re-enables on exit).</summary>
    public bool GameBoosterDisableGameBar
    {
        get => ReadBool(nameof(GameBoosterDisableGameBar), defaultValue: false);
        set => WriteBool(nameof(GameBoosterDisableGameBar), value);
    }

    /// <summary>Switch to the High Performance power plan for the duration of the game session.</summary>
    public bool GameBoosterHighPerfPowerPlan
    {
        get => ReadBool(nameof(GameBoosterHighPerfPowerPlan), defaultValue: false);
        set => WriteBool(nameof(GameBoosterHighPerfPowerPlan), value);
    }

    /// <summary>
    /// Tune the Windows Multimedia System Profile (GPU Priority, Scheduling Category,
    /// SystemResponsiveness=0) for gaming.
    /// Default: false — can cause screen tearing on some systems; opt-in only.
    /// </summary>
    public bool GameBoosterGpuProfile
    {
        // Defaults ON. This is the single most valuable thing a boost does, and before v0.7.281
        // the process priority raise ran unconditionally — the toggle only gated the MMCSS
        // profile. Gating the priority raise behind a setting that defaulted to false meant a
        // game was detected and then nothing happened to it.
        get => ReadBool(nameof(GameBoosterGpuProfile), defaultValue: true);
        set => WriteBool(nameof(GameBoosterGpuProfile), value);
    }

    // ── GPU/multimedia-profile saved originals (crash-safe restore) ──────────────
    // Persisted so the MMCSS "Games" profile is restored even if Systema is killed while a
    // boost is active. -1 / "" mean the value did NOT exist before boost (→ delete on restore).
    public bool MmProfileSavedActive
    {
        get => ReadBool(nameof(MmProfileSavedActive), defaultValue: false);
        set => WriteBool(nameof(MmProfileSavedActive), value);
    }
    public int MmProfileSavedPriority
    {
        get => ReadInt(nameof(MmProfileSavedPriority), defaultValue: -1);
        set => WriteInt(nameof(MmProfileSavedPriority), value);
    }
    public string MmProfileSavedSchedCategory
    {
        get => ReadString(nameof(MmProfileSavedSchedCategory), defaultValue: "") ?? "";
        set => WriteString(nameof(MmProfileSavedSchedCategory), value);
    }
    public string MmProfileSavedSfioPriority
    {
        get => ReadString(nameof(MmProfileSavedSfioPriority), defaultValue: "") ?? "";
        set => WriteString(nameof(MmProfileSavedSfioPriority), value);
    }

    /// <summary>Pause the Windows Search indexer while a game is boosting (resume after). Default on.</summary>
    public bool GameBoosterPauseIndexing
    {
        get => ReadBool(nameof(GameBoosterPauseIndexing), defaultValue: true);
        set => WriteBool(nameof(GameBoosterPauseIndexing), value);
    }

    /// <summary>Disable Nagle's algorithm (TcpAckFrequency + TCPNoDelay) for lower online-game latency.</summary>
    public bool GameBoosterDisableNagle
    {
        get => ReadBool(nameof(GameBoosterDisableNagle), defaultValue: true);
        set => WriteBool(nameof(GameBoosterDisableNagle), value);
    }

    /// <summary>Flush the DNS resolver cache when a game session starts.</summary>
    public bool GameBoosterFlushDns
    {
        get => ReadBool(nameof(GameBoosterFlushDns), defaultValue: false);
        set => WriteBool(nameof(GameBoosterFlushDns), value);
    }

    /// <summary>Disable network adapter power management while gaming (restored on exit).</summary>
    public bool GameBoosterNicPowerSaving
    {
        get => ReadBool(nameof(GameBoosterNicPowerSaving), defaultValue: true);
        set => WriteBool(nameof(GameBoosterNicPowerSaving), value);
    }

    /// <summary>Disable Wi-Fi when ethernet is detected at game start (restored on exit).</summary>
    public bool GameBoosterDisableWifiOnEthernet
    {
        get => ReadBool(nameof(GameBoosterDisableWifiOnEthernet), defaultValue: false);
        set => WriteBool(nameof(GameBoosterDisableWifiOnEthernet), value);
    }

    /// <summary>Disable Bluetooth radio at game start (restored on exit, only if it was on).</summary>
    public bool GameBoosterDisableBluetooth
    {
        get => ReadBool(nameof(GameBoosterDisableBluetooth), defaultValue: false);
        set => WriteBool(nameof(GameBoosterDisableBluetooth), value);
    }

    /// <summary>
    /// Prevent the system from sleeping while a game session or manual boost is active.
    /// Uses SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) — same mechanism as
    /// video players. Restored to normal sleep timeouts the moment the boost ends.
    /// Default: true (on).
    /// </summary>
    public bool GameBoosterPreventSleep
    {
        get => ReadBool(nameof(GameBoosterPreventSleep), defaultValue: true);
        set => WriteBool(nameof(GameBoosterPreventSleep), value);
    }

    // GameBoosterDisableSearchIndexing was removed: it duplicated GameBoosterPauseIndexing and
    // nothing ever read it, so the toggle backing it moved a value that changed nothing.

    /// <summary>
    /// On supported laptops (Dell, Lenovo, …), pause or limit battery charging while a
    /// boost session is active. On laptops with a small AC adapter the power budget is
    /// shared between charging and CPU+GPU; pausing charging gives the full adapter
    /// wattage to performance and lowers chassis temps. Vendor-specific — feature is
    /// hidden on desktops and unsupported brands. Default: false (opt-in).
    /// </summary>
    public bool GameBoosterPauseCharging
    {
        get => ReadBool(nameof(GameBoosterPauseCharging), defaultValue: false);
        set => WriteBool(nameof(GameBoosterPauseCharging), value);
    }

    // ── Intel iGPU panel ──────────────────────────────────────────────────────

    /// <summary>
    /// Opt-in: when true, Systema re-applies the saved Intel iGPU profile at startup.
    /// Intel driver updates sometimes wipe these registry values, so this lets the
    /// user's chosen settings survive a driver upgrade. Off by default — we never
    /// re-write display-adapter keys unless the user explicitly asked us to.
    /// </summary>
    public bool IntelGpuReapplyEnabled
    {
        get => ReadBool(nameof(IntelGpuReapplyEnabled), defaultValue: false);
        set => WriteBool(nameof(IntelGpuReapplyEnabled), value);
    }

    /// <summary>
    /// JSON map of Intel managed value-name → int the user last applied. Used to
    /// re-apply after a driver update when <see cref="IntelGpuReapplyEnabled"/> is on.
    /// Null = nothing saved yet.
    /// </summary>
    public Dictionary<string, int>? IntelGpuProfile
    {
        get
        {
            var json = ReadString(nameof(IntelGpuProfile), defaultValue: null);
            if (json == null) return null;
            try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json); }
            catch { return null; }
        }
        set
        {
            if (value == null) DeleteValue(nameof(IntelGpuProfile));
            else WriteString(nameof(IntelGpuProfile), JsonSerializer.Serialize(value));
        }
    }

    /// <summary>Opt-in: re-apply the NVIDIA power-management choice after driver updates
    /// (which wipe the display-adapter values). Off by default.</summary>
    public bool NvidiaGpuReapplyEnabled
    {
        get => ReadBool(nameof(NvidiaGpuReapplyEnabled), defaultValue: false);
        set => WriteBool(nameof(NvidiaGpuReapplyEnabled), value);
    }

    /// <summary>True when the user last chose "prefer maximum performance" for the NVIDIA GPU.
    /// Re-applied on startup when <see cref="NvidiaGpuReapplyEnabled"/> is on.</summary>
    public bool NvidiaGpuPreferMaxPerformance
    {
        get => ReadBool(nameof(NvidiaGpuPreferMaxPerformance), defaultValue: false);
        set => WriteBool(nameof(NvidiaGpuPreferMaxPerformance), value);
    }

    // ── System Stability tweaks ───────────────────────────────────────────────

    /// <summary>Whether the user has disabled Windows Fast Startup (off by default).</summary>
    public bool FastStartupDisabled
    {
        get => ReadBool(nameof(FastStartupDisabled), defaultValue: false);
        set => WriteBool(nameof(FastStartupDisabled), value);
    }

    /// <summary>Whether the user has disabled NTFS last-access timestamp updates (off by default).</summary>
    public bool NtfsLastAccessDisabled
    {
        get => ReadBool(nameof(NtfsLastAccessDisabled), defaultValue: false);
        set => WriteBool(nameof(NtfsLastAccessDisabled), value);
    }

    /// <summary>
    /// Whether the user has opted into maximum system responsiveness
    /// (MMCSS SystemResponsiveness = 0) via System Tweaks. Off by default. When ON,
    /// GameBooster's legacy VSync self-heal stops forcing SystemResponsiveness back to
    /// 20 — the user's 0 is honoured instead. Kept in sync with the live registry value
    /// by ToolsViewModel (and adopted at startup by GameBooster if a 0 already exists).
    /// </summary>
    public bool MaxResponsivenessEnabled
    {
        get => ReadBool(nameof(MaxResponsivenessEnabled), defaultValue: false);
        set => WriteBool(nameof(MaxResponsivenessEnabled), value);
    }

    /// <summary>Foreground priority boost (Win32PrioritySeparation=38). Off by default.</summary>
    public bool ForegroundBoostEnabled
    {
        get => ReadBool(nameof(ForegroundBoostEnabled), defaultValue: false);
        set => WriteBool(nameof(ForegroundBoostEnabled), value);
    }

    /// <summary>Disable Windows 11 suggestions / spotlight / setup nags. Off by default;
    /// when the user turns it on it is reinforced on launch so the nags never come back.</summary>
    public bool DisableSuggestionsEnabled
    {
        get => ReadBool(nameof(DisableSuggestionsEnabled), defaultValue: false);
        set => WriteBool(nameof(DisableSuggestionsEnabled), value);
    }

    /// <summary>Disable Bing/web results in Start search. Off by default.</summary>
    public bool DisableWebSearchEnabled
    {
        get => ReadBool(nameof(DisableWebSearchEnabled), defaultValue: false);
        set => WriteBool(nameof(DisableWebSearchEnabled), value);
    }


    // ── Sleep → Hibernate ────────────────────────────────────────────────────

    /// <summary>
    /// Whether the Sleep → Hibernate feature is currently enabled.
    /// When true, the laptop hibernates after <see cref="SleepToHibernateMinutes"/> of
    /// sleep on battery power. Default: false.
    /// </summary>
    public bool SleepToHibernateEnabled
    {
        get => ReadBool(nameof(SleepToHibernateEnabled), defaultValue: false);
        set => WriteBool(nameof(SleepToHibernateEnabled), value);
    }

    /// <summary>
    /// Sleep → Hibernate timeout in minutes (battery only). Default: 30.
    /// </summary>
    public int SleepToHibernateMinutes
    {
        get => ReadInt(nameof(SleepToHibernateMinutes), defaultValue: 30);
        set => WriteInt(nameof(SleepToHibernateMinutes), Math.Max(1, value));
    }

    /// <summary>
    /// Whether the Sleep → Hibernate feature is enabled on AC power (plugged in).
    /// Default: false.
    /// </summary>
    public bool SleepToHibernateAcEnabled
    {
        get => ReadBool(nameof(SleepToHibernateAcEnabled), defaultValue: false);
        set => WriteBool(nameof(SleepToHibernateAcEnabled), value);
    }

    /// <summary>
    /// Sleep → Hibernate timeout in minutes (AC / plugged-in). Default: 30.
    /// </summary>
    public int SleepToHibernateAcMinutes
    {
        get => ReadInt(nameof(SleepToHibernateAcMinutes), defaultValue: 30);
        set => WriteInt(nameof(SleepToHibernateAcMinutes), Math.Max(1, value));
    }

    // ── Auto-Pilot Mode ───────────────────────────────────────────────────────

    /// <summary>
    /// Fired (on whatever thread writes the property) whenever <see cref="AutoPilotModeEnabled"/>
    /// changes. ViewModels subscribe so their <c>IsAutoPilotActive</c> property updates live
    /// without needing a polling refresh.
    /// </summary>
    public static event EventHandler? AutoPilotModeChanged;

    /// <summary>
    /// Fired after Auto-Pilot's <c>RunAutoPilotAsync</c> finishes (whether the
    /// user toggled the mode ON, clicked "Apply settings once," or it was
    /// triggered by a drift re-check). Used by ServicesViewModel to refresh
    /// the merged "Privacy &amp; Background Services" toggle so it reflects
    /// the post-cleanup state — without this, the refresh fires when
    /// AutoPilotModeChanged is raised at the START of the run, before the
    /// service-disable calls actually complete, and the toggle stays stuck OFF.
    /// </summary>
    public static event EventHandler? OptimizationsApplied;
    public static void RaiseOptimizationsApplied() =>
        OptimizationsApplied?.Invoke(null, EventArgs.Empty);

    /// <summary>
    /// When true, Auto-Pilot Mode is active: all Auto-Pilot-managed settings are locked,
    /// their controls are grayed out in every view, and any drift is auto-healed every 30 s.
    /// Persisted to HKCU so the mode survives updates and restarts. Default: false.
    /// </summary>
    public bool AutoPilotModeEnabled
    {
        get => ReadBool(nameof(AutoPilotModeEnabled), defaultValue: false);
        set
        {
            WriteBool(nameof(AutoPilotModeEnabled), value);
            AutoPilotModeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    // ── Updates ───────────────────────────────────────────────────────────────

    /// <summary>
    /// When true (default), Systema checks for updates every 2 hours and installs
    /// silently when the CPU is idle and not in game mode.
    /// When false, all automatic update activity is suppressed (manual check still works).
    /// </summary>
    public bool AutoUpdateEnabled
    {
        get => ReadBool(nameof(AutoUpdateEnabled), defaultValue: true);
        set => WriteBool(nameof(AutoUpdateEnabled), value);
    }

    public bool KeepSystemaRunning
    {
        get => ReadBool(nameof(KeepSystemaRunning), defaultValue: false);
        set => WriteBool(nameof(KeepSystemaRunning), value);
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    // Single lock serializes all concurrent registry reads and writes to prevent
    // torn state when multiple threads (game check timer, UI thread) access simultaneously.
    private static readonly object _registryLock = new();

    private static bool ReadBool(string name, bool defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key?.GetValue(name) is int v) return v != 0;
            }
            catch { }
            return defaultValue;
        }
    }

    private static void WriteBool(string name, bool value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    private static int ReadInt(string name, int defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key?.GetValue(name) is int v) return v;
            }
            catch { }
            return defaultValue;
        }
    }

    private static void WriteInt(string name, int value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    private static string? ReadString(string name, string? defaultValue)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                return key?.GetValue(name) as string ?? defaultValue;
            }
            catch { return defaultValue; }
        }
    }

    private static void WriteString(string name, string value)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                key?.SetValue(name, value, RegistryValueKind.String);
            }
            catch { }
        }
    }

    private static void DeleteValue(string name)
    {
        lock (_registryLock)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { }
        }
    }

    // ── Export / Import ──────────────────────────────────────────────────────

    /// <summary>
    /// Serialises all values under HKCU\Software\Systema to an indented JSON string.
    /// DWORD values are exported as numbers; strings as strings.
    /// </summary>
    public string ExportToJson()
    {
        lock (_registryLock)
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKey);
                if (key != null)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        var val = key.GetValue(name);
                        dict[name] = val;
                    }
                }
            }
            catch { }
            return JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    /// <summary>
    /// Imports settings from a JSON string produced by <see cref="ExportToJson"/>.
    /// Unknown keys are silently ignored. Returns true on success.
    /// </summary>
    public bool ImportFromJson(string json)
    {
        lock (_registryLock)
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                if (dict == null) return false;

                using var key = Registry.CurrentUser.CreateSubKey(RegistryKey, writable: true);
                if (key == null) return false;

                foreach (var (name, element) in dict)
                {
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Number when element.TryGetInt32(out int iv):
                            key.SetValue(name, iv, RegistryValueKind.DWord);
                            break;
                        case JsonValueKind.String:
                            key.SetValue(name, element.GetString() ?? string.Empty, RegistryValueKind.String);
                            break;
                        case JsonValueKind.True:
                            key.SetValue(name, 1, RegistryValueKind.DWord);
                            break;
                        case JsonValueKind.False:
                            key.SetValue(name, 0, RegistryValueKind.DWord);
                            break;
                    }
                }
                return true;
            }
            catch { return false; }
        }
    }
}
