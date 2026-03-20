// ════════════════════════════════════════════════════════════════════════════
// GameBoosterService.cs  ·  Auto-detects 20+ game processes and applies boost
// ════════════════════════════════════════════════════════════════════════════
//
// Monitors running processes on a DispatcherTimer to detect game launches and
// exits. On game launch, applies boost (kills configured services, raises process
// priority); on exit, restores everything. Ships with a built-in list of 20+
// known game executables. Auto-boost can be toggled; state is persisted via
// SettingsService.
//
// RELATED FILES
//   ServiceControlService.cs   — kills and restores the service kill list
//   SettingsService.cs         — persists auto-boost enabled flag
//   Models/KillListEntry.cs    — game process entry for kill/restore list
//   GameBoosterViewModel.cs    — UI binding and per-game enable/disable
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Systema.Core;
using static Systema.Core.ThreadHelper;

namespace Systema.Services;

/// <summary>
/// Smart Game Booster — detects games, boosts while gaming, auto-restores after.
/// </summary>
public sealed class GameBoosterService : IDisposable
{
    private static readonly LoggerService _log = LoggerService.Instance;

    private readonly ServiceControlService _serviceControl;
    private readonly SettingsService       _settings;
    private readonly ProcessLassoService   _processLasso;

    private DispatcherTimer? _gameCheckTimer;
    private DispatcherTimer? _xboxCheckTimer;
    private TrayService?     _tray;

    // ── State ──────────────────────────────────────────────────────────────────
    private bool _boostActive;
    private bool _manualBoostActive;
    private DateTime _manualBoostStartedAt;
    private DispatcherTimer? _manualBoostTimeoutTimer;
    private readonly List<string> _killedServices = new();
    private readonly object _lock = new();
    private string? _boostedProcessName;

    // ── P/Invoke: Timer resolution (winmm) ────────────────────────────────────
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    // ── P/Invoke: IO priority ──────────────────────────────────────────────────
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref int processInformation, int processInformationLength);

    private const int ProcessIoPriority = 33;
    private const int IoPriorityHigh   = 3;
    private const int IoPriorityNormal = 2;

    // ── P/Invoke: Working set trim (for free-memory-on-boost) ─────────────────
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwAccess, bool bInherit, int dwPid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Flush modified pages + purge standby list to maximise immediately-free RAM.
    // Requires SeProfileSingleProcessPrivilege (available to admin processes).
    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int SystemInformationClass,
        ref uint SystemInformation, uint SystemInformationLength);

    private const int  SystemMemoryListInformation = 80;
    private const uint MemoryFlushModifiedList     = 1; // move modified pages → standby
    private const uint MemoryPurgeStandbyList      = 2; // evict standby list → free

    private const uint PROCESS_SET_INFORMATION = 0x0200;

    // ── Registry paths for new boost options ──────────────────────────────────
    private const string NotificationKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string HighPerfPlanGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string BalancedPlanGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string GameDvrKey       = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameConfigKey    = @"System\GameConfigStore";
    private const string MmProfileKey     = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmGamesKey       = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string TcpipIfacesKey   = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string NicClassKey      = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    // ── Saved pre-boost state for restore ─────────────────────────────────────
    private int?   _savedNotificationsEnabled; // null = notifications were already off — don't restore
    private string? _savedPowerPlanGuid;       // null = not changed
    // Timer resolution
    private bool   _timerResolutionSet;
    // Game Bar / DVR
    private int?   _savedAppCaptureEnabled;
    private int?   _savedGameDvrEnabled;
    // Multimedia profile
    private int?    _savedSystemResponsiveness;
    private int?    _savedMmPriority;
    private string? _savedSchedulingCategory;
    private string? _savedSfIoPriority;
    // Nagle / NIC power — list of (HKLM-relative path, value name, original value or null=delete)
    private List<(string path, string name, object? val)>? _nagleRestore;
    private List<(string path, string name, object? val)>? _nicPowerRestore;
    // Wi-Fi disable — names of adapters we disabled (null = feature not used / no adapters disabled)
    private List<string>? _disabledWifiAdapters;

    public bool IsEnabled             => _settings.GameBoosterEnabled;
    public bool BoostActive           => _boostActive;
    public bool ManualBoostActive     => _manualBoostActive;
    public DateTime ManualBoostStartedAt => _manualBoostStartedAt;
    public bool GamesInstalled        { get; private set; }
    public string? ActiveGameName     { get; private set; }

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action<string>? BoostActivated;   // passes game name
    public event Action?         BoostDeactivated;
    public event Action<bool>?   GamesInstalledChanged;
    public event Action?         ManualBoostTimedOut;

    // ── Well-known game executables ────────────────────────────────────────────
    private static readonly string[] KnownGameProcesses =
    {
        // Roblox
        "RobloxPlayerBeta", "RobloxPlayer",
        // Fortnite
        "FortniteClient-Win64-Shipping",
        // Minecraft
        "javaw",             // Minecraft Java Edition (also modded packs)
        "Minecraft.Windows", // Minecraft Bedrock
        // CS2 / CS:GO
        "csgo", "cs2",
        // GTA
        "GTA5",
        // Tom Clancy's
        "RainbowSix",        // Rainbow Six Siege
        // Valorant / League
        "valorant", "VALORANT-Win64-Shipping",
        "LeagueOfLegends",
        // Escape From Tarkov
        "EscapeFromTarkov",
        // RPGs
        "BG3",               // Baldur's Gate 3
        "Cyberpunk2077",
        "eldenring",
        // Open-world / survival
        "DyingLightGame",
        "rust",              // Rust
        "ShooterGame",       // ARK: Survival Evolved / Ascended
        "Terraria",
        // Shooters
        "r5apex",            // Apex Legends
        "TslGame",           // PUBG: Battlegrounds
        "Overwatch",         // Overwatch 2
        "DeadByDaylight",
        // MMOs / online
        "ffxiv_dx11",        // Final Fantasy XIV
        "wow", "wow_classic",// World of Warcraft
        "GW2-64",            // Guild Wars 2
        // Strategy
        "sc2",               // StarCraft II
        "AoE2DE",            // Age of Empires 2 DE
        // Racing
        "ForzaHorizon5",
        "AC2-Win64-Shipping",// Assetto Corsa Competizione
        // Other popular titles
        "Warframe.x64", "Warframe",
        "dota2",
    };

    // ── Well-known game install paths / registry keys ──────────────────────────
    private static readonly string[] GameInstallRegistryKeys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Roblox",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Epic Games Launcher",
        @"SOFTWARE\Valve\Steam",
        @"SOFTWARE\WOW6432Node\Valve\Steam",
        @"SOFTWARE\Mojang\InstalledProducts\Minecraft Launcher",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Minecraft Launcher",
        @"SOFTWARE\Microsoft\XboxApp",
        @"SOFTWARE\Riot Games\League of Legends",
        @"SOFTWARE\Riot Games\VALORANT",
    };

    private static readonly string[] GameInstallFolders =
    {
        @"C:\Program Files (x86)\Roblox",
        @"C:\Program Files\Roblox",
        @"C:\Program Files\Epic Games",
        @"C:\Program Files (x86)\Steam",
        @"C:\Program Files\Steam",
        @"C:\Program Files (x86)\Battle.net",
        @"C:\Program Files\Battle.net",
    };

    // ── Anti-cheat / engine detection ─────────────────────────────────────────
    private static readonly string[] AntiCheatProcesses =
    {
        "vgc",           // Vanguard
        "EasyAntiCheat",
        "BEService",     // BattlEye
    };

    // ── Default services to kill during boost ─────────────────────────────────
    private static readonly string[] DefaultKillList =
    {
        "Spooler",           // Print Spooler
        "Fax",               // Fax service
        "TabletInputService",// Touch keyboard
        "WSearch",           // Windows Search
        "SysMain",           // SuperFetch
        "DiagTrack",         // Telemetry
        "WerSvc",            // Error reporting
        "MapsBroker",        // Maps manager
        "lfsvc",             // Geolocation
        "RetailDemo",        // Retail demo service
        "XblGameSave",       // Xbox Live game save
        "XboxNetApiSvc",     // Xbox Live networking
        "BITS",              // Background Intelligent Transfer — stops background downloads
        "DoSvc",             // Delivery Optimization — stops P2P bandwidth use
        "ssdpsrv",           // SSDP Discovery — stops UPnP scanning overhead
        "upnphost",          // UPnP Device Host — not needed while gaming
        "TrkWks",            // Distributed Link Tracking — small I/O overhead
        "PcaSvc",            // Program Compatibility Assistant — CPU overhead on app launch
        "WMPNetworkSvc",     // Windows Media Player Network Sharing — unnecessary while gaming
        "NcaSvc",            // Network Connectivity Assistant — not needed while gaming
    };

    // ── Constructor ────────────────────────────────────────────────────────────

    public GameBoosterService(ServiceControlService serviceControl, SettingsService settings, ProcessLassoService processLasso)
    {
        _serviceControl = serviceControl;
        _settings       = settings;
        _processLasso   = processLasso;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void StartMonitoring(TrayService tray)
    {
        _tray = tray;

        // Initial game install scan (large-stack thread — Process.GetProcesses() needs it)
        _ = RunOnLargeStackAsync(ScanForInstalledGames);

        // Game process monitor (configurable, default 2 min — minimum 1 min to avoid spin)
        var intervalMin = Math.Max(1, _settings.GameCheckIntervalMinutes);
        _gameCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(intervalMin)
        };
        _gameCheckTimer.Tick += (_, _) => _ = RunOnLargeStackAsync(CheckRunningGames);
        _gameCheckTimer.Start();

        // Xbox service check every 4 hours
        _xboxCheckTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(4)
        };
        _xboxCheckTimer.Tick += (_, _) => _ = CheckXboxServicesAsync();
        _xboxCheckTimer.Start();

        _log.Info("GameBoosterService", $"Monitoring started (interval: {_settings.GameCheckIntervalMinutes} min)");
    }

    public void UpdateCheckInterval(int minutes)
    {
        if (_gameCheckTimer != null)
            _gameCheckTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    /// <summary>Force an immediate game scan + boost check.</summary>
    public async Task ForceCheckAsync()
    {
        await RunOnLargeStackAsync(ScanForInstalledGames);
        await RunOnLargeStackAsync(CheckRunningGames);
    }

    /// <summary>
    /// Manually activates game boost regardless of game detection.
    /// Auto-disables after 6 hours to prevent leaving services killed indefinitely.
    /// </summary>
    public void EnableManualBoost()
    {
        if (!_settings.GameBoosterEnabled) return; // master switch
        _manualBoostActive = true;
        _manualBoostStartedAt = DateTime.UtcNow;

        if (!_boostActive)
        {
            Action? postLockAction;
            lock (_lock) postLockAction = ActivateBoost("Manual Boost");
            postLockAction?.Invoke();
        }

        // 6-hour auto-off timer — always restart it so re-enabling resets the clock
        _manualBoostTimeoutTimer?.Stop();
        _manualBoostTimeoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(6)
        };
        _manualBoostTimeoutTimer.Tick += (_, _) =>
        {
            _manualBoostTimeoutTimer?.Stop();
            _log.Info("GameBoosterService", "Manual boost auto-disabled after 6 hours");
            DisableManualBoost();
            ManualBoostTimedOut?.Invoke();
            _tray?.ShowBalloon("Game Boost", "Manual boost auto-disabled after 6 hours.",
                System.Windows.Forms.ToolTipIcon.Info);
        };
        _manualBoostTimeoutTimer.Start();

        _log.Info("GameBoosterService", "Manual boost enabled (auto-off in 6 hours)");
    }

    /// <summary>Manually deactivates boost (also cancels the 6-hour timer).</summary>
    public void DisableManualBoost()
    {
        _manualBoostActive = false;
        _manualBoostTimeoutTimer?.Stop();
        _manualBoostTimeoutTimer = null;

        // Only deactivate if no real game is currently running
        if (_boostActive && FindRunningGame() == null)
        {
            Action? postLockAction;
            lock (_lock) postLockAction = DeactivateBoost();
            postLockAction?.Invoke();
        }

        _log.Info("GameBoosterService", "Manual boost disabled");
    }

    /// <summary>
    /// Enables or disables the game booster master switch.
    /// Immediately deactivates any active boost (including manual) when disabling.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Cancel manual boost timer
            if (_manualBoostActive)
            {
                _manualBoostActive = false;
                _manualBoostTimeoutTimer?.Stop();
                _manualBoostTimeoutTimer = null;
            }
            // Deactivate any running boost
            if (_boostActive)
            {
                Action? postLockAction;
                lock (_lock) postLockAction = DeactivateBoost();
                postLockAction?.Invoke();
            }
        }
        _log.Info("GameBoosterService", $"Game Booster master switch → {(enabled ? "ON" : "OFF")}");
    }

    /// <summary>Returns the effective kill list (user overrides or default).</summary>
    public List<string> GetKillList() => _settings.GameBoosterKillList ?? new List<string>(DefaultKillList);

    public void SetKillList(List<string> list) => _settings.GameBoosterKillList = list;

    // ── Game Detection ─────────────────────────────────────────────────────────

    public bool ScanForInstalledGames()
    {
        bool found = CheckRegistryForGames() || CheckFolderForGames() || CheckAntiCheatPresent();
        if (found != GamesInstalled)
        {
            GamesInstalled = found;
            _log.Info("GameBoosterService", $"Games installed: {found}");
            GamesInstalledChanged?.Invoke(found);

            if (!found)
                _ = CheckXboxServicesAsync();
        }
        return found;
    }

    private static bool CheckRegistryForGames()
    {
        foreach (var key in GameInstallRegistryKeys)
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(key)
                             ?? Registry.CurrentUser.OpenSubKey(key);
                if (reg != null) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool CheckFolderForGames()
    {
        foreach (var folder in GameInstallFolders)
        {
            try
            {
                if (Directory.Exists(folder)) return true;
            }
            catch { }
        }
        return false;
    }

    private static bool CheckAntiCheatPresent()
    {
        try
        {
            var procs = Process.GetProcesses();
            foreach (var proc in procs)
            {
                try
                {
                    foreach (var ac in AntiCheatProcesses)
                        if (proc.ProcessName.Contains(ac, StringComparison.OrdinalIgnoreCase))
                            return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    // ── Running Game Detection ─────────────────────────────────────────────────

    private void CheckRunningGames()
    {
        if (!_settings.GameBoosterEnabled) return; // master switch

        try
        {
            // FindRunningGame runs outside the lock (Process.GetProcesses is expensive),
            // but the shouldActivate/shouldDeactivate decision is made inside the lock
            // to avoid racing with ActivateBoost/DeactivateBoost from concurrent callers.
            string? detectedGame = FindRunningGame();

            // Events and tray calls are captured as actions and fired OUTSIDE the lock
            // to prevent deadlocks if UI event handlers call back into this service.
            Action? postLockAction = null;
            lock (_lock)
            {
                bool shouldActivate = detectedGame != null && !_boostActive;
                // Never auto-deactivate while manual boost is on — user controls it explicitly
                bool shouldDeactivate = detectedGame == null && _boostActive && !_manualBoostActive;

                if (shouldActivate)
                    postLockAction = ActivateBoost(detectedGame!);
                else if (shouldDeactivate)
                    postLockAction = DeactivateBoost();
            }
            postLockAction?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("GameBoosterService", "Game check failed", ex);
        }
    }

    private static string? FindRunningGame()
    {
        try
        {
            var procs = Process.GetProcesses();
            foreach (var proc in procs)
            {
                try
                {
                    foreach (var game in KnownGameProcesses)
                        if (proc.ProcessName.Equals(game, StringComparison.OrdinalIgnoreCase))
                            return game;

                    // Check anti-cheats as a proxy for a game running
                    foreach (var ac in AntiCheatProcesses)
                        if (proc.ProcessName.Contains(ac, StringComparison.OrdinalIgnoreCase))
                            return "Unknown Game (Anti-Cheat detected)";
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── Boost Activation ──────────────────────────────────────────────────────

    /// <summary>
    /// Activates game boost. Must be called inside <see cref="_lock"/>.
    /// Returns an <see cref="Action"/> containing UI event and tray notifications that must be
    /// invoked AFTER releasing the lock to prevent deadlocks with UI event handlers.
    /// </summary>
    private Action? ActivateBoost(string gameName)
    {
        _boostActive   = true;
        ActiveGameName = gameName;
        _killedServices.Clear();

        _log.Info("GameBoosterService", $"Boost activated for: {gameName}");

        CrashGuard.Mark($"Game Boost activating for: {gameName} — killing services...");

        var killList = GetKillList();
        foreach (var svcName in killList)
        {
            try
            {
                using var svc = new ServiceController(svcName);
                // Refresh status before reading to avoid stale state
                svc.Refresh();
                if (svc.Status == ServiceControllerStatus.Running)
                {
                    svc.Stop();
                    // Poll instead of WaitForStatus to avoid deep native stack
                    PollForStatus(svc, ServiceControllerStatus.Stopped, timeoutSeconds: 5);
                    _killedServices.Add(svcName);
                    _log.Info("GameBoosterService", $"Killed service: {svcName}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("GameBoosterService", $"Could not kill {svcName}: {ex.Message}");
            }
        }

        CrashGuard.Mark($"Game Boost active — {_killedServices.Count} services paused for {gameName}: {string.Join(", ", _killedServices)}");

        // Boost game process priority (skip anti-cheat/unknown placeholders)
        bool isRealGame = !gameName.Contains("Anti-Cheat", StringComparison.OrdinalIgnoreCase)
                       && !gameName.Contains("Unknown Game", StringComparison.OrdinalIgnoreCase);
        if (isRealGame)
            BoostGameProcess(gameName);

        // Apply new boost options (memory, notifications, power plan)
        ApplyBoostOptions(gameName);

        // Return UI/tray notifications as an action to fire outside the lock.
        // Firing events inside a lock risks deadlock if any UI handler calls back into this service.
        var capturedGameName = gameName;
        return () =>
        {
            BoostActivated?.Invoke(capturedGameName);
            _tray?.SetTooltip($"Systema — Boosting: {capturedGameName}");
            _tray?.ShowBalloon("Game Boost Active",
                $"Boosting for {capturedGameName}. Non-essential services suspended.",
                System.Windows.Forms.ToolTipIcon.Info);
        };
    }

    /// <summary>
    /// Deactivates game boost. Must be called inside <see cref="_lock"/>.
    /// Returns an <see cref="Action"/> containing UI event and tray notifications that must be
    /// invoked AFTER releasing the lock to prevent deadlocks with UI event handlers.
    /// </summary>
    private Action? DeactivateBoost()
    {
        // Restore game process priority before clearing state
        if (ActiveGameName != null)
        {
            bool isRealGame = !ActiveGameName.Contains("Anti-Cheat", StringComparison.OrdinalIgnoreCase)
                           && !ActiveGameName.Contains("Unknown Game", StringComparison.OrdinalIgnoreCase);
            if (isRealGame)
                RestoreGameProcess(ActiveGameName);
        }

        _boostActive   = false;
        ActiveGameName = null;

        // Restore new boost options before restoring services
        RestoreBoostOptions();

        _log.Info("GameBoosterService", "Game session ended — restoring services");

        foreach (var svcName in _killedServices)
        {
            try
            {
                using var svc = new ServiceController(svcName);
                svc.Refresh();
                if (svc.Status == ServiceControllerStatus.Stopped)
                {
                    svc.Start();
                    _log.Info("GameBoosterService", $"Restored service: {svcName}");
                }
            }
            catch (Exception ex)
            {
                _log.Warn("GameBoosterService", $"Could not restore {svcName}: {ex.Message}");
            }
        }
        _killedServices.Clear();
        CrashGuard.Clear();

        // Return UI/tray notifications as an action to fire outside the lock.
        return () =>
        {
            BoostDeactivated?.Invoke();
            _tray?.SetTooltip("Systema — Windows Optimizer");
            _tray?.ShowBalloon("Game Boost Ended", "Services restored to normal.", System.Windows.Forms.ToolTipIcon.Info);
        };
    }

    /// <summary>
    /// Polls service status in a flat loop rather than calling WaitForStatus,
    /// which uses kernel waits that can exhaust stack space on threadpool threads.
    /// </summary>
    private static void PollForStatus(ServiceController svc, ServiceControllerStatus target, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            svc.Refresh();
            if (svc.Status == target) return;
            Thread.Sleep(200);
        }
    }

    // ── Process Priority Boost ─────────────────────────────────────────────────

    private void BoostGameProcess(string gameName)
    {
        try
        {
            var procs = Process.GetProcessesByName(gameName);
            foreach (var proc in procs)
            {
                try
                {
                    proc.PriorityClass = ProcessPriorityClass.High;
                    int ioPriority = IoPriorityHigh;
                    NtSetInformationProcess(proc.Handle, ProcessIoPriority, ref ioPriority, sizeof(int));
                    _boostedProcessName = $"{gameName}.exe";
                    if (_processLasso.IsInstalled())
                        _processLasso.ExcludeFromProBalance(_boostedProcessName);
                    _log.Info("GameBoosterService", $"Priority boosted for {gameName} (High CPU + IO)");
                }
                catch (Exception ex)
                {
                    _log.Warn("GameBoosterService", $"Priority boost failed for {gameName}: {ex.Message}");
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"BoostGameProcess error: {ex.Message}");
        }
    }

    private void RestoreGameProcess(string gameName)
    {
        try
        {
            var procs = Process.GetProcessesByName(gameName);
            foreach (var proc in procs)
            {
                try
                {
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                    int ioPriority = IoPriorityNormal;
                    NtSetInformationProcess(proc.Handle, ProcessIoPriority, ref ioPriority, sizeof(int));
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

        if (_boostedProcessName != null && _processLasso.IsInstalled())
        {
            _processLasso.RemoveProBalanceExclusion(_boostedProcessName);
            _boostedProcessName = null;
        }

        _log.Info("GameBoosterService", $"Process priority restored for {gameName}");
    }

    // ── New Boost Options ─────────────────────────────────────────────────────

    private void ApplyBoostOptions(string gameName)
    {
        // 0. New network / system options (applied before the heavy RAM trim)
        if (_settings.GameBoosterTimerResolution)  ApplyTimerResolution();
        if (_settings.GameBoosterDisableGameBar)   ApplyGameBarDisable();
        if (_settings.GameBoosterGpuProfile)       ApplyMultimediaProfile();
        if (_settings.GameBoosterDisableNagle)          ApplyDisableNagle();
        if (_settings.GameBoosterFlushDns)              FlushDns();
        if (_settings.GameBoosterNicPowerSaving)        ApplyNicPowerSaving();
        if (_settings.GameBoosterDisableWifiOnEthernet) ApplyDisableWifi();

        // 1. Aggressively trim RAM from background processes:
        //    Step 1 — per-process: remove working-set floor then flush pages to standby list.
        //    Step 2 — system-wide: flush modified pages, then purge the standby list so all
        //             those pages become immediately free (not just potentially reusable).
        if (_settings.GameBoosterFreeMemory)
        {
            try
            {
                var procs = Process.GetProcesses();
                int trimmed = 0;
                foreach (var proc in procs)
                {
                    try
                    {
                        if (proc.ProcessName.Equals(gameName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (proc.Id <= 4) continue;
                        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, proc.Id);
                        if (h == IntPtr.Zero) continue;
                        try
                        {
                            // Remove the working-set floor so the OS can trim to zero pages,
                            // then immediately flush remaining pages to the standby list.
                            SetProcessWorkingSetSize(h, (IntPtr)(-1), (IntPtr)(-1));
                            EmptyWorkingSet(h);
                            trimmed++;
                        }
                        finally { CloseHandle(h); }
                    }
                    catch { }
                    finally { try { proc.Dispose(); } catch { } }
                }

                // Flush the modified-page list so dirty pages move to standby, then
                // purge the standby list to turn standby pages into free pages the game
                // can allocate without waiting for the memory manager to recycle them.
                try
                {
                    uint cmd = MemoryFlushModifiedList;
                    NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(uint));
                    cmd = MemoryPurgeStandbyList;
                    NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(uint));
                    _log.Info("GameBoosterService", "System standby list purged");
                }
                catch (Exception ex2) { _log.Warn("GameBoosterService", $"StandbyPurge failed: {ex2.Message}"); }

                _log.Info("GameBoosterService", $"Freed memory from {trimmed} background processes");
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"FreeMemory failed: {ex.Message}"); }
        }

        // 3. Suppress notifications (disable toast notifications) — only if they're currently ON.
        //    If the user already had them off, we must not restore to ON when the boost ends.
        if (_settings.GameBoosterSuppressNotifications)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NotificationKey, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(NotificationKey, writable: true);
                if (key != null)
                {
                    var currentValue = key.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") as int?;
                    // null = key absent = notifications ON by default; 1 = explicitly ON; 0 = already OFF
                    bool alreadyOff = currentValue.HasValue && currentValue.Value == 0;
                    if (!alreadyOff)
                    {
                        _savedNotificationsEnabled = currentValue ?? 1; // treat missing-key as ON
                        key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", 0, RegistryValueKind.DWord);
                        _log.Info("GameBoosterService", "Notifications suppressed");
                    }
                    else
                    {
                        _savedNotificationsEnabled = null; // already off — nothing to restore
                        _log.Info("GameBoosterService", "Notifications already off — skipping suppress");
                    }
                }
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"SuppressNotifications failed: {ex.Message}"); }
        }

        // 4. Switch to High Performance power plan
        if (_settings.GameBoosterHighPerfPowerPlan)
        {
            try
            {
                // Save current active scheme
                var getActive = new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var getProc = System.Diagnostics.Process.Start(getActive);
                if (getProc == null)
                {
                    _log.Warn("GameBoosterService", "powercfg /getactivescheme failed to start — skipping power plan switch");
                }
                else
                {
                    string? output = getProc.StandardOutput.ReadToEnd();
                    getProc.WaitForExit();
                    // Output: "Power Scheme GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (High performance)"
                    if (output != null)
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(
                            output, @"GUID:\s+([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})");
                        if (match.Success) _savedPowerPlanGuid = match.Groups[1].Value;
                    }

                    // Switch to High Performance
                    var set = new System.Diagnostics.ProcessStartInfo(
                        "powercfg", $"/setactive {HighPerfPlanGuid}")
                    {
                        UseShellExecute = false, CreateNoWindow = true
                    };
                    System.Diagnostics.Process.Start(set)?.WaitForExit();
                    _log.Info("GameBoosterService", $"Switched to High Performance power plan (was: {_savedPowerPlanGuid ?? "unknown"})");
                }
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"HighPerfPowerPlan failed: {ex.Message}"); }
        }
    }

    private void RestoreBoostOptions()
    {
        // 0. Restore new options (order: reverse of apply)
        RestoreWifi();
        RestoreNicPowerSaving();
        RestoreNagle();
        RestoreMultimediaProfile();
        if (_savedAppCaptureEnabled.HasValue || _savedGameDvrEnabled.HasValue) RestoreGameBarDvr();
        RestoreTimerResolution();

        // 1. Restore notifications — only if we actually suppressed them (were ON before boost)
        if (_savedNotificationsEnabled.HasValue)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NotificationKey, writable: true);
                if (key != null)
                    key.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", _savedNotificationsEnabled.Value, RegistryValueKind.DWord);
                _log.Info("GameBoosterService", "Notifications restored");
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreNotifications failed: {ex.Message}"); }
            finally { _savedNotificationsEnabled = null; }
        }

        // 2. Restore power plan
        if (_savedPowerPlanGuid != null)
        {
            try
            {
                var restore = new System.Diagnostics.ProcessStartInfo(
                    "powercfg", $"/setactive {_savedPowerPlanGuid}")
                {
                    UseShellExecute = false, CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(restore)?.WaitForExit();
                _log.Info("GameBoosterService", $"Power plan restored to {_savedPowerPlanGuid}");
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"RestorePowerPlan failed: {ex.Message}"); }
            finally { _savedPowerPlanGuid = null; }
        }
    }

    // ── New Boost Helpers ─────────────────────────────────────────────────────

    // ·· 1ms Timer Resolution ··················································

    private void ApplyTimerResolution()
    {
        // timeBeginPeriod returns 0 (TIMERR_NOERROR) on success
        if (timeBeginPeriod(1) == 0)
        {
            _timerResolutionSet = true;
            _log.Info("GameBoosterService", "Timer resolution set to 1 ms");
        }
        else
        {
            _log.Warn("GameBoosterService", "timeBeginPeriod(1) failed — timer resolution unchanged");
        }
    }

    private void RestoreTimerResolution()
    {
        if (!_timerResolutionSet) return;
        timeEndPeriod(1);
        _timerResolutionSet = false;
        _log.Info("GameBoosterService", "Timer resolution restored to Windows default");
    }

    // ·· Game Bar & DVR ························································

    private void ApplyGameBarDisable()
    {
        try
        {
            using var dvrKey = Registry.CurrentUser.OpenSubKey(GameDvrKey, writable: true);
            if (dvrKey != null)
            {
                var cur = dvrKey.GetValue("AppCaptureEnabled");
                _savedAppCaptureEnabled = cur is int i ? i : 1;
                if (_savedAppCaptureEnabled != 0)
                    dvrKey.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"GameDVR key: {ex.Message}"); }

        try
        {
            using var cfgKey = Registry.CurrentUser.OpenSubKey(GameConfigKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(GameConfigKey);
            if (cfgKey != null)
            {
                var cur = cfgKey.GetValue("GameDVR_Enabled");
                _savedGameDvrEnabled = cur is int i ? i : 1;
                if (_savedGameDvrEnabled != 0)
                    cfgKey.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"GameConfigStore key: {ex.Message}"); }

        _log.Info("GameBoosterService", "Game Bar & DVR disabled");
    }

    private void RestoreGameBarDvr()
    {
        try
        {
            if (_savedAppCaptureEnabled.HasValue)
            {
                using var dvrKey = Registry.CurrentUser.OpenSubKey(GameDvrKey, writable: true);
                dvrKey?.SetValue("AppCaptureEnabled", _savedAppCaptureEnabled.Value, RegistryValueKind.DWord);
                _savedAppCaptureEnabled = null;
            }
        }
        catch { }

        try
        {
            if (_savedGameDvrEnabled.HasValue)
            {
                using var cfgKey = Registry.CurrentUser.OpenSubKey(GameConfigKey, writable: true);
                cfgKey?.SetValue("GameDVR_Enabled", _savedGameDvrEnabled.Value, RegistryValueKind.DWord);
                _savedGameDvrEnabled = null;
            }
        }
        catch { }

        _log.Info("GameBoosterService", "Game Bar & DVR restored");
    }

    // ·· GPU / Multimedia System Profile ·······································

    private void ApplyMultimediaProfile()
    {
        try
        {
            // Parent profile key — SystemResponsiveness=0 gives the game all CPU quanta
            using var profKey = Registry.LocalMachine.OpenSubKey(MmProfileKey, writable: true);
            if (profKey != null)
            {
                var cur = profKey.GetValue("SystemResponsiveness");
                _savedSystemResponsiveness = cur is int i ? i : 20;
                if (_savedSystemResponsiveness != 0)
                    profKey.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
            }

            // Games task sub-key — raise scheduling priority and GPU/SFIO priority
            using var gamesKey = Registry.LocalMachine.OpenSubKey(MmGamesKey, writable: true);
            if (gamesKey != null)
            {
                var curPri = gamesKey.GetValue("Priority");
                _savedMmPriority = curPri is int ip ? ip : 2;

                _savedSchedulingCategory = gamesKey.GetValue("Scheduling Category") as string ?? "Medium";
                _savedSfIoPriority       = gamesKey.GetValue("SFIO Priority")       as string ?? "Normal";

                gamesKey.SetValue("Priority",            6,      RegistryValueKind.DWord);
                gamesKey.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                gamesKey.SetValue("SFIO Priority",       "High", RegistryValueKind.String);
            }

            _log.Info("GameBoosterService", "Multimedia system profile tuned for gaming");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyMultimediaProfile: {ex.Message}"); }
    }

    private void RestoreMultimediaProfile()
    {
        try
        {
            if (_savedSystemResponsiveness.HasValue)
            {
                using var profKey = Registry.LocalMachine.OpenSubKey(MmProfileKey, writable: true);
                profKey?.SetValue("SystemResponsiveness", _savedSystemResponsiveness.Value, RegistryValueKind.DWord);
                _savedSystemResponsiveness = null;
            }

            if (_savedMmPriority.HasValue)
            {
                using var gamesKey = Registry.LocalMachine.OpenSubKey(MmGamesKey, writable: true);
                if (gamesKey != null)
                {
                    gamesKey.SetValue("Priority",            _savedMmPriority.Value,      RegistryValueKind.DWord);
                    gamesKey.SetValue("Scheduling Category", _savedSchedulingCategory!,    RegistryValueKind.String);
                    gamesKey.SetValue("SFIO Priority",       _savedSfIoPriority!,          RegistryValueKind.String);
                }
                _savedMmPriority = null;
                _savedSchedulingCategory = null;
                _savedSfIoPriority = null;
            }

            _log.Info("GameBoosterService", "Multimedia system profile restored");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreMultimediaProfile: {ex.Message}"); }
    }

    // ·· Nagle's Algorithm ·····················································

    private void ApplyDisableNagle()
    {
        _nagleRestore = new List<(string, string, object?)>();
        try
        {
            using var ifacesKey = Registry.LocalMachine.OpenSubKey(TcpipIfacesKey);
            if (ifacesKey == null) return;

            foreach (var guid in ifacesKey.GetSubKeyNames())
            {
                var path = $@"{TcpipIfacesKey}\{guid}";
                try
                {
                    using var iKey = Registry.LocalMachine.OpenSubKey(path, writable: true);
                    if (iKey == null) continue;

                    var savedAck   = iKey.GetValue("TcpAckFrequency");
                    var savedDelay = iKey.GetValue("TCPNoDelay");
                    iKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    iKey.SetValue("TCPNoDelay",      1, RegistryValueKind.DWord);
                    _nagleRestore.Add((path, "TcpAckFrequency", savedAck));
                    _nagleRestore.Add((path, "TCPNoDelay",      savedDelay));
                }
                catch { }
            }

            _log.Info("GameBoosterService",
                $"Nagle disabled on {_nagleRestore.Count / 2} TCP adapter(s)");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyDisableNagle: {ex.Message}"); }
    }

    private void RestoreNagle()
    {
        if (_nagleRestore == null) return;
        foreach (var (path, name, val) in _nagleRestore)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key == null) continue;
                if (val == null)
                    key.DeleteValue(name, throwOnMissingValue: false);
                else
                    key.SetValue(name, Convert.ToInt32(val), RegistryValueKind.DWord);
            }
            catch { }
        }
        _nagleRestore = null;
        _log.Info("GameBoosterService", "Nagle algorithm restored");
    }

    // ·· Flush DNS ·············································· ···············

    private void FlushDns()
    {
        try
        {
            using var ps = new Process();
            ps.StartInfo = new ProcessStartInfo
            {
                FileName               = "ipconfig.exe",
                Arguments              = "/flushdns",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            ps.Start();
            ps.WaitForExit(3000);
            _log.Info("GameBoosterService", "DNS resolver cache flushed");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"FlushDns: {ex.Message}"); }
    }

    // ·· NIC Power Saving ······················································

    private void ApplyNicPowerSaving()
    {
        _nicPowerRestore = new List<(string, string, object?)>();
        try
        {
            using var nicClass = Registry.LocalMachine.OpenSubKey(NicClassKey);
            if (nicClass == null) return;

            foreach (var subName in nicClass.GetSubKeyNames())
            {
                // Skip non-numeric sub-keys (e.g. "Properties")
                if (!int.TryParse(subName, out _)) continue;

                var path = $@"{NicClassKey}\{subName}";
                try
                {
                    using var adapterKey = Registry.LocalMachine.OpenSubKey(path, writable: true);
                    if (adapterKey == null) continue;

                    // Presence of NetCfgInstanceId confirms this is a network adapter
                    if (adapterKey.GetValue("NetCfgInstanceId") == null) continue;

                    var savedVal = adapterKey.GetValue("PnPCapabilities");
                    // 24 (0x18) = disable "allow computer to turn off device" + wake flags
                    adapterKey.SetValue("PnPCapabilities", 24, RegistryValueKind.DWord);
                    _nicPowerRestore.Add((path, "PnPCapabilities", savedVal));
                }
                catch { }
            }

            _log.Info("GameBoosterService",
                $"NIC power saving disabled on {_nicPowerRestore.Count} adapter(s)");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyNicPowerSaving: {ex.Message}"); }
    }

    private void RestoreNicPowerSaving()
    {
        if (_nicPowerRestore == null) return;
        foreach (var (path, name, val) in _nicPowerRestore)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                if (key == null) continue;
                if (val == null)
                    key.DeleteValue(name, throwOnMissingValue: false);
                else
                    key.SetValue(name, Convert.ToInt32(val), RegistryValueKind.DWord);
            }
            catch { }
        }
        _nicPowerRestore = null;
        _log.Info("GameBoosterService", "NIC power saving restored");
    }

    // ·· Disable Wi-Fi when Ethernet is active ··································

    private void ApplyDisableWifi()
    {
        _disabledWifiAdapters = null;
        try
        {
            // Only disable Wi-Fi when at least one ethernet/wired adapter is up
            bool ethernetUp = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                       && n.OperationalStatus    == OperationalStatus.Up);

            if (!ethernetUp)
            {
                _log.Info("GameBoosterService", "DisableWifi: no active ethernet — skipping");
                return;
            }

            // Collect Wi-Fi adapters that are currently up
            var wifiAdapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                         && n.OperationalStatus    == OperationalStatus.Up)
                .Select(n => n.Name)
                .ToList();

            if (wifiAdapters.Count == 0)
            {
                _log.Info("GameBoosterService", "DisableWifi: no active Wi-Fi adapters found");
                return;
            }

            _disabledWifiAdapters = new List<string>();
            foreach (var name in wifiAdapters)
            {
                try
                {
                    var psi = new ProcessStartInfo("netsh",
                        $"interface set interface \"{name}\" disable")
                    { UseShellExecute = false, CreateNoWindow = true };
                    Process.Start(psi)?.WaitForExit(3000);
                    _disabledWifiAdapters.Add(name);
                    _log.Info("GameBoosterService", $"DisableWifi: disabled '{name}'");
                }
                catch (Exception ex)
                {
                    _log.Warn("GameBoosterService", $"DisableWifi: failed on '{name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyDisableWifi failed: {ex.Message}"); }
    }

    private void RestoreWifi()
    {
        if (_disabledWifiAdapters == null || _disabledWifiAdapters.Count == 0) return;
        foreach (var name in _disabledWifiAdapters)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh",
                    $"interface set interface \"{name}\" enable")
                { UseShellExecute = false, CreateNoWindow = true };
                Process.Start(psi)?.WaitForExit(3000);
                _log.Info("GameBoosterService", $"RestoreWifi: re-enabled '{name}'");
            }
            catch (Exception ex)
            {
                _log.Warn("GameBoosterService", $"RestoreWifi: failed on '{name}': {ex.Message}");
            }
        }
        _disabledWifiAdapters = null;
    }

    // ── Xbox Services Logic ────────────────────────────────────────────────────

    private static readonly string[] XboxServices =
    {
        "XboxGipSvc", "xbgm", "XblAuthManager", "XblGameSave", "XboxNetApiSvc"
    };

    private async Task CheckXboxServicesAsync()
    {
        // If user has overridden Xbox setting, respect it
        if (_settings.XboxServicesUserOverride) return;

        if (GamesInstalled)
        {
            _log.Info("GameBoosterService", "Games installed — keeping Xbox services enabled");
            return;
        }

        _log.Info("GameBoosterService", "No games found — disabling Xbox services");
        foreach (var svc in XboxServices)
        {
            try { await _serviceControl.DisableServiceAsync(svc); }
            catch { }
        }
    }

    // ── Dispose ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _gameCheckTimer?.Stop();
        _xboxCheckTimer?.Stop();
        _manualBoostTimeoutTimer?.Stop();

        if (_boostActive)
            DeactivateBoost()?.Invoke();
    }
}
