// ════════════════════════════════════════════════════════════════════════════
// GameBoosterService.cs  ·  Auto-detects games, anti-cheats, and game-related services
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
    private readonly BatteryPauseService   _batteryPause;

    private DispatcherTimer? _gameCheckTimer;
    // Fast foreground poll (see FastForegroundGameCheck). The slow timer above stays as the safety
    // net for anything the foreground path can't see — a borderless game that never takes focus,
    // an anti-cheat launching before its game, or a title that exits without a focus change.
    private DispatcherTimer? _fastGameTimer;
    private string? _lastForegroundName;
    private TrayService?     _tray;

    // ── State ──────────────────────────────────────────────────────────────────
    private bool _boostActive;
    private bool _manualBoostActive;
    // Game the user explicitly switched boost OFF for. Auto-boost stays suppressed for the rest of
    // THAT game's session, then re-arms on its own: the monitor clears this as soon as the detected
    // game changes or the game exits, so the next session boosts normally without the user having to
    // remember anything. Turning boost back on manually also clears it.
    private string? _autoBoostSuppressedFor;
    private DateTime _manualBoostStartedAt;
    private DispatcherTimer? _manualBoostTimeoutTimer;
    private readonly List<string> _killedServices  = new();
    private readonly object _lock = new();
    private string? _boostedProcessName;
    /// <summary>True when the active boost is for a real, named game process whose priority we
    /// raised — so DeactivateBoost knows to restore it without re-parsing the display name.</summary>
    private bool _boostedRealGame;
    /// <summary>The match the active boost was started for — keeps the session alive across alt-tab.</summary>
    private GameMatch? _activeMatch;
    // pid → the process's CPU priority class BEFORE we boosted it, so RestoreGameProcess
    // hands back the real original instead of blindly forcing Normal. A game launched at
    // (say) High by its launcher, or already adjusted by another tool, would otherwise be
    // demoted to Normal when the boost ends — the same "lost original priority" bug the
    // Task Sleep / Launch Boost path had.

    // ── P/Invoke: Sleep prevention (kernel32) ─────────────────────────────────
    // SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) — same call video
    // players use to stop the machine sleeping mid-playback. ES_CONTINUOUS makes the
    // request persistent (survives thread death) until explicitly cleared by calling
    // SetThreadExecutionState(ES_CONTINUOUS) with no other flags.
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS      = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    // ── P/Invoke: IO priority ──────────────────────────────────────────────────
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(
        IntPtr processHandle, int processInformationClass,
        ref int processInformation, int processInformationLength);

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

    // SeDebugPrivilege — needed to OpenProcess(PROCESS_SUSPEND_RESUME) on SearchIndexer, which
    // runs as NT AUTHORITY\SYSTEM. Elevation alone isn't enough; without this privilege enabled
    // the open is access-denied and the pause silently no-ops. Admins have the privilege in their
    // token but it's disabled by default — we enable it once, best-effort.
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020, TOKEN_QUERY = 0x0008, SE_PRIVILEGE_ENABLED = 0x0002;
    private static bool _debugPrivilegeEnabled;

    private void EnsureDebugPrivilege()
    {
        if (_debugPrivilegeEnabled) return;
        _debugPrivilegeEnabled = EnablePrivilege("SeDebugPrivilege");
        if (!_debugPrivilegeEnabled)
            _log.Warn("GameBoosterService", "SeDebugPrivilege not granted (indexer pause may not work)");
    }

    /// <summary>Turns on one named privilege in our own token. True when it actually took.</summary>
    private bool EnablePrivilege(string privilegeName)
    {
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, privilegeName, out LUID luid)) return false;

                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                // AdjustTokenPrivileges returns true even on partial success — verify via last error.
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(token); }
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"EnablePrivilege({privilegeName}) failed: {ex.Message}");
            return false;
        }
    }

    // Flush modified pages + purge standby list to maximise immediately-free RAM.
    // Requires SeProfileSingleProcessPrivilege (available to admin processes).
    [DllImport("ntdll.dll")]
    private static extern uint NtSetSystemInformation(int SystemInformationClass,
        ref uint SystemInformation, uint SystemInformationLength);

    // Used to measure ullAvailPhys before and after the trim so the log reports
    // an actual MB-freed number instead of just "trimmed N processes" — diagnostics
    // are directly comparable with MemoryService.FreeRam now.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint  dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    /// <summary>Returns currently-available physical RAM in MB. 0 on failure.</summary>
    private static long GetAvailableRamMb()
    {
        try
        {
            var info = new MEMORYSTATUSEX();
            return GlobalMemoryStatusEx(info) ? (long)(info.ullAvailPhys / (1024 * 1024)) : 0;
        }
        catch { return 0; }
    }

    private const int  SystemMemoryListInformation = 80;
    private const uint MemoryFlushModifiedList     = 1; // move modified pages → standby
    private const uint MemoryPurgeStandbyList      = 2; // evict standby list → free

    private const uint PROCESS_SET_INFORMATION   = 0x0200;
    /// <summary>Enough to read a protected process's basics; PROCESS_QUERY_INFORMATION is not.</summary>
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    // EmptyWorkingSet and SetProcessWorkingSetSize require BOTH of the rights
    // below (per psapi.dll docs). Using PROCESS_SET_INFORMATION alone made every
    // call silently fail with ERROR_ACCESS_DENIED — the loop still incremented
    // its counter and logged "Trimmed N processes", but Task Manager showed
    // zero RAM actually freed. Bug fixed v0.7.9.
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SET_QUOTA         = 0x0100;
    private const uint PROCESS_TRIM_WORKING_SET  = PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA;

    // ── VSync-critical processes (NEVER trim these) ──────────────────────────
    // Trimming dwm/audiodg/svchost/GPU-vendor working sets forces them to page
    // memory from disk next time they run, which causes the compositor thread to
    // miss its 60/144/240 Hz presentation deadlines and NVIDIA MPO / Independent
    // Flip to fall back to composed mode — causing the very tearing this Game
    // Booster is supposed to prevent. Keep in sync with
    // MemoryService.VsyncCriticalProcessNames.
    private static readonly HashSet<string> VsyncCriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm",                    // Desktop Window Manager — THE VSync killer when trimmed
        "audiodg",                // Audio Device Graph Isolation — feeds MMCSS boost to DWM
        "svchost",                // hosts DWM helpers, Themes, UxSms, AudioSrv, etc.
        "nvcontainer",            // NVIDIA user-mode GPU scheduler / telemetry
        "nvdisplay.container",    // NVIDIA display container
        "nvwmi64",                // NVIDIA WMI
        "RadeonSoftware",         // AMD driver user-mode
        "amdow",                  // AMD overlay
        "atieclxx", "atiesrxx",   // AMD kernel-mode helpers
        "igfxEM", "igfxCUIService", // Intel GPU helpers
        "csrss",                  // Client/Server Runtime — critical session manager
        "services",               // SCM
        "lsass",                  // credential manager — memory corruption risk if trimmed
        "winlogon",               // session / DWM parent
        "wininit", "smss",        // early-boot session managers
        "System", "Registry", "Idle", "Secure System", "Memory Compression",
    };

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
    // Sleep prevention — true if SetThreadExecutionState(ES_SYSTEM_REQUIRED) is currently active
    private bool _sleepPrevented;
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
    // Wi-Fi disable — true if we turned the software radio off via WLAN API
    private bool _wifiRadioDisabled;
    // Bluetooth disable — true if we turned the radio off (so we know to restore it)
    private bool _bluetoothRadioDisabled;
    // Search indexing — true if WSearch was running before boost and we stopped it
    private bool _searchIndexingWasRunning;
    // Battery pause snapshot — non-null if charging was paused via vendor BIOS hook
    private BatteryPauseSnapshot? _batteryPauseSnapshot;

    // ── Crash-recovery persistence ───────────────────────────────────────────
    // On boost activation we write a JSON snapshot of all pre-boost originals.
    // On deactivation we delete it. If Systema starts and this file exists, the
    // previous session crashed mid-boost — we load the snapshot and restore.
    private static readonly string BoostStateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Systema");
    private static readonly string BoostStatePath = Path.Combine(BoostStateDir, "boost_state.json");

    /// <summary>Serializable snapshot of pre-boost state for crash recovery.</summary>
    private sealed class BoostStateSnapshot
    {
        public string? GameName                   { get; set; }
        public List<string>? KilledServices       { get; set; }
        public int? NotificationsEnabled          { get; set; }
        public string? PowerPlanGuid              { get; set; }
        public bool SearchIndexingWasRunning      { get; set; }
        public int? AppCaptureEnabled             { get; set; }
        public int? GameDvrEnabled                { get; set; }
        public int? SystemResponsiveness          { get; set; }
        public int? MmPriority                    { get; set; }
        public string? SchedulingCategory         { get; set; }
        public string? SfIoPriority               { get; set; }
        public List<RegistryRestoreEntry>? NagleRestore    { get; set; }
        public List<RegistryRestoreEntry>? NicPowerRestore { get; set; }
        public bool WifiRadioDisabled             { get; set; }
        public bool BluetoothRadioDisabled        { get; set; }
        // Battery pause — vendor mode active before pause. Null if pause was not applied.
        public string? BatteryPauseMethod       { get; set; }
        public string? BatteryPauseVendor       { get; set; }
        public string? BatteryPauseOriginalMode { get; set; }
        public bool    BatteryPauseWasActive    { get; set; }
    }

    private sealed class RegistryRestoreEntry
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int? Val    { get; set; }  // null = delete value on restore
    }

    public bool IsEnabled             => _settings.GameBoosterEnabled;
    public bool BoostActive           => _boostActive;
    public bool ManualBoostActive     => _manualBoostActive;
    public DateTime ManualBoostStartedAt => _manualBoostStartedAt;
    public bool GamesInstalled        { get; private set; }
    public string? ActiveGameName     { get; private set; }

    // ── Session reporting ──────────────────────────────────────────────────────
    // Purely observational: these describe what ActivateBoost already did, so the tab can show a
    // real session (game, elapsed, what got applied) instead of a status string. Nothing here
    // changes behaviour.
    public DateTime? BoostStartedAt { get; private set; }
    /// <summary>Current battery %, or null on a desktop / when it can't be read.</summary>
    public int? BatteryPercent => _batteryPause.GetBatteryPercent();

    // ── Events ─────────────────────────────────────────────────────────────────
    public event Action<string>? BoostActivated;   // passes game name
    public event Action?         BoostDeactivated;
    public event Action<bool>?   GamesInstalledChanged;
    public event Action?         ManualBoostTimedOut;

    // ── Well-known game executables ────────────────────────────────────────────
    // A HashSet, not an array: this is probed once per running process on every detection
    // pass, so an O(1) lookup beats walking ~40 strings each time.
    private static readonly HashSet<string> KnownGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Roblox
        "RobloxPlayerBeta", "RobloxPlayer",
        // Minecraft (the Java edition is handled by TitleQualifiedGames below, because its
        // process name on its own is not enough to call it a game)
        "Minecraft.Windows", // Minecraft Bedrock
        // CS2 / CS:GO
        "csgo", "cs2",
        // GTA
        "GTA5",
        // Tom Clancy's
        "RainbowSix",        // Rainbow Six Siege
        // Valorant / League
        "valorant",
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
        // Other popular titles
        "Warframe.x64", "Warframe",
        "dota2",
        // Driving / sim
        "BeamNG.drive.x64", "BeamNG.drive",
        "ForzaHorizon4", "ForzaMotorsport",
        "AssettoCorsa", "acs",
        "DirtRally2",
        "eurotrucks2", "amtrucks",   // Euro Truck Simulator 2 / American Truck Simulator
        "MudRunner", "SnowRunner",
        // Shooters
        "cod",                        // modern Call of Duty launcher exe
        "BF2042", "bf1", "bf4",
        "destiny2",
        "helldivers2",
        "TheFinals",
        "PayDay3",
        "Warthunder", "aces",         // War Thunder
        // Survival / sandbox
        "DayZ_x64",
        "arma3_x64", "ArmaReforger",
        "Palworld",
        "Valheim",
        "ProjectZomboid64",
        "7DaysToDie",
        "SonsOfTheForest",
        "Stationeers",
        "factorio",
        "SpaceEngineers",
        "NMS",                        // No Man's Sky
        // RPG / action
        "RDR2",
        "Diablo IV",
        "PathOfExile_x64", "PathOfExileSteam", "PathOfExile", "PathOfExile_x64Steam",
        "sekiro", "DarkSoulsIII", "armoredcore6",
        "MonsterHunterWorld", "MonsterHunterWilds", "MonsterHunterRise",
        "nier automata", "SpaceMarine2",
        // Co-op / party
        "Lethal Company",
        "Phasmophobia",
        "REPO",
        "Among Us",
        "Content Warning",
        "Risk of Rain 2",
        // Racing / sports
        "RocketLeague",
        "FC25", "FC24",               // EA Sports FC
        "NBA2K25",
        // Gacha / anime
        "GenshinImpact", "YuanShen",
        "StarRail",
        "ZenlessZoneZero",
        // Strategy / management
        "Cities2",
        "Civ6", "Civ7",
        "Stellaris", "hoi4", "eu4", "ck3",
        "RimWorld", "RimWorldWin64",
        // Other
        "HaloInfinite",
        "SeaOfThieves",
        "TS4_x64",                    // The Sims 4
        "Stardew Valley",
        "balatro",
        "VampireSurvivors",
        "WorldOfTanks", "WorldOfWarships",
    };

    /// <summary>
    /// Suffixes that identify a packaged game build rather than one specific title.
    /// Unreal Engine ships every game as "&lt;Name&gt;-Win64-Shipping", so one entry here covers
    /// Fortnite, Valorant, Assetto Corsa Competizione and several hundred titles that would
    /// otherwise each need a line above (and would each be missed until someone added one).
    /// </summary>
    private static readonly string[] KnownGameSuffixes =
    {
        "-Win64-Shipping",
        "-Win32-Shipping",
        "-WinGDK-Shipping",  // Game Pass / Microsoft Store packaging
    };

    /// <summary>
    /// Processes that are only a game when the window title says so.
    /// "javaw" is Minecraft Java — and also IntelliJ, Ghidra, JDownloader and every other Java
    /// desktop app. Boosting (switching the power plan, killing Wi-Fi) because an IDE is open is
    /// a worse failure than being slow to notice Minecraft, so the title has to confirm it.
    /// </summary>
    private static readonly (string Process, string TitleContains)[] TitleQualifiedGames =
    {
        ("javaw", "Minecraft"),
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
    //
    // Used by FindRunningGame() to detect a gaming session via anti-cheat proxy
    // (when the game exe itself isn't in KnownGameProcesses).
    //
    // ALSO used by ApplyBoostOptions() to skip these processes during working-set
    // trim — kernel-mode AC drivers (Vanguard, EAC, BattlEye, etc.) can intercept
    // per-process memory API calls (OpenProcess, EmptyWorkingSet) and react with an
    // unrecoverable AccessViolation that terminates our process, or flag Systema in
    // the AC's telemetry and trigger a game ban. Never call memory trim APIs on them.
    private static readonly string[] AntiCheatProcesses =
    {
        "vgc",           // Valorant Vanguard (kernel service, always running)
        "EasyAntiCheat", // Easy Anti-Cheat  (Epic, many titles)
        "BEService",     // BattlEye         (PUBG, Rainbow Six, Arma, DayZ)
        "nProtect",      // nProtect GameGuard (Korean MMOs, Lost Ark)
        "GameMon",       // GameGuard monitor process
        "PnkBstrA",      // PunkBuster A     (Battlefield, legacy CoD)
        "PnkBstrB",      // PunkBuster B
        "FACEITClient",  // FACEIT AC        (CS2 competitive)
        "mhyprot",       // miHoYo AC        (Genshin Impact, Honkai: Star Rail)
        "xhunter1",      // XIGNCODE3        (Warface, Black Desert Online)
        "ESEAClient",    // ESEA             (CS2 competitive platform)
    };

    // ── Constructor ────────────────────────────────────────────────────────────

    public GameBoosterService(ServiceControlService serviceControl, SettingsService settings,
                              ProcessLassoService processLasso, BatteryPauseService batteryPause)
    {
        _serviceControl = serviceControl;
        _settings       = settings;
        _processLasso   = processLasso;
        _batteryPause   = batteryPause;

    }

    /// <summary>
    /// Exposes the BatteryPauseService so the UI can show vendor / support text
    /// without re-running detection. Read-only.
    /// </summary>
    public BatteryPauseService BatteryPause => _batteryPause;

    // ── Public API ─────────────────────────────────────────────────────────────

    public void StartMonitoring(TrayService tray)
    {
        _tray = tray;

        // VSync repair: normalize SystemResponsiveness if it's stuck at 0 from an older
        // Systema build. Runs unconditionally on every startup — it's a no-op unless
        // the value is actually 0, and it cannot harm a healthy system.
        RepairVSyncCriticalRegistryValues();

        // Crash recovery: if the previous session crashed while boost was active,
        // restore all settings to their pre-boost originals before doing anything else.
        RecoverBoostStateFromCrash();
        RestoreMultimediaProfile();   // crash-safe MMCSS restore (no-op unless a boost was left active)
        ResumeIndexing();             // crash-safe: un-pause the Search indexer if a boost left it suspended

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

        // Fast path: boost now starts/stops within seconds of a game taking or losing the screen,
        // instead of waiting out the slow timer.
        _fastGameTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _fastGameTimer.Tick += (_, _) => FastForegroundGameCheck();
        _fastGameTimer.Start();

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
    public async Task EnableManualBoostAsync()
    {
        if (!_settings.GameBoosterEnabled) return; // master switch
        _manualBoostActive = true;
        _manualBoostStartedAt = DateTime.UtcNow;
        // Turning it back on cancels an opt-out from earlier in this game's session, so auto-boost
        // behaves normally again straight away.
        lock (_lock) _autoBoostSuppressedFor = null;

        if (!_boostActive)
        {
            // ActivateBoost kills services, writes registry, and flushes memory from 200+
            // processes — must NOT run on the UI thread or it will freeze the window.
            Action? postLockAction = await Task.Run(() =>
            {
                // IsKnownGame: false — "Manual Boost" is a label, not a process name.
                lock (_lock) return ActivateBoost(new GameMatch("Manual Boost", IsKnownGame: false));
            }).ConfigureAwait(true); // resume on UI thread for the DispatcherTimer below
            postLockAction?.Invoke();
        }

        // 6-hour auto-off timer — must be created on the UI thread (we're back on it after await)
        _manualBoostTimeoutTimer?.Stop();
        _manualBoostTimeoutTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(6)
        };
        _manualBoostTimeoutTimer.Tick += async (_, _) =>
        {
            _manualBoostTimeoutTimer?.Stop();
            _log.Info("GameBoosterService", "Manual boost auto-disabled after 6 hours");
            await DisableManualBoostAsync();
            ManualBoostTimedOut?.Invoke();
            _tray?.ShowBalloon("Game Boost", "Manual boost auto-disabled after 6 hours.",
                System.Windows.Forms.ToolTipIcon.Info);
        };
        _manualBoostTimeoutTimer.Start();

        _log.Info("GameBoosterService", "Manual boost enabled (auto-off in 6 hours)");
    }

    /// <summary>
    /// Forces a clean deactivation of the boost before a Systema update installs.
    /// Unlike the normal paths, this deactivates regardless of whether a game is still
    /// running — we must restore system state before the installer replaces the exe.
    /// After deactivation, waits up to 8 s so OS service start commands can propagate
    /// (svc.Start() is fire-and-forget at the SCM level; without this pause the installer
    /// could replace DLLs while services are still transitioning to Running).
    /// On return the boost snapshot is deleted and the system is back to pre-boost state.
    /// </summary>
    public async Task DeactivateForUpdateAsync()
    {
        if (!_boostActive) return;

        _log.Info("GameBoosterService", "Pre-update: deactivating game boost before installer launches");

        // Stop the check timer so it cannot try to re-activate while we're restoring
        _gameCheckTimer?.Stop();
        _fastGameTimer?.Stop();
        _manualBoostActive = false;
        _manualBoostTimeoutTimer?.Stop();
        _manualBoostTimeoutTimer = null;

        // DeactivateBoost() must run off the UI thread (restores services, writes registry)
        Action? postAction = await Task.Run(() =>
        {
            lock (_lock) return DeactivateBoost();
        }).ConfigureAwait(true);
        postAction?.Invoke();

        // Give the OS time for service start commands to take effect.
        // 8 s covers even slow services like WSearch / BITS (typically < 3 s).
        _log.Info("GameBoosterService", "Pre-update: boost deactivated — waiting for services to start");
        await Task.Delay(8_000);
        _log.Info("GameBoosterService", "Pre-update deactivation complete — installer may proceed");
    }

    /// <summary>Manually deactivates boost (also cancels the 6-hour timer).</summary>
    public async Task DisableManualBoostAsync()
    {
        _manualBoostActive = false;
        _manualBoostTimeoutTimer?.Stop();
        _manualBoostTimeoutTimer = null;

        // FindRunningGame calls Process.GetProcesses() and DeactivateBoost restores services —
        // both can block for several seconds, so run off the UI thread.
        if (_boostActive)
        {
            Action? postLockAction = await Task.Run(() =>
            {
                // Turning boost off is an explicit instruction, so honour it even while a game is
                // running. This used to bail out (`if (FindRunningGame() != null) return null;`),
                // which meant the tray toggle silently did nothing during an auto-started session —
                // boost stayed on and the menu flipped back. Remember which game the user opted out
                // of so the monitor doesn't just switch it straight back on a tick later.
                string? running = FindRunningGame()?.Name;
                lock (_lock)
                {
                    _autoBoostSuppressedFor = running;   // null when no game — nothing to suppress
                    return DeactivateBoost();
                }
            }).ConfigureAwait(true);
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

    /// <summary>
    /// Public entry-point for Settings → Reset All Settings to force the VSync-critical
    /// registry repair (SystemResponsiveness normalization) without waiting for the next
    /// startup. Safe to call anytime — no-op unless the value is actually 0.
    /// </summary>
    public void RepairRegistryNow() => RepairVSyncCriticalRegistryValues();

    // ── Game Detection ─────────────────────────────────────────────────────────

    /// <summary>Display name used when anti-cheat proves a game is running but can't name it.</summary>
    private const string UnknownGameName = "Unknown Game (Anti-Cheat detected)";

    /// <summary>
    /// What detection found. <paramref name="IsKnownGame"/> is false for the anti-cheat fallback
    /// and for manual boost, where <paramref name="Name"/> is a label rather than a process — so
    /// nothing downstream has to infer that by reading the string.
    ///
    /// <paramref name="Pid"/> is the exact process that matched (0 when there isn't one). The
    /// boost used to throw this away and re-look-up by name, which was fragile in both directions:
    /// for Minecraft it meant boosting EVERY javaw on the machine including an open IDE, and if
    /// the name lookup came back empty the game was detected and then silently never boosted.
    /// </summary>
    private readonly record struct GameMatch(string Name, bool IsKnownGame, int Pid = 0);

    public bool ScanForInstalledGames()
    {
        bool found = CheckRegistryForGames() || CheckFolderForGames() || CheckAntiCheatPresent();
        if (found != GamesInstalled)
        {
            GamesInstalled = found;
            _log.Info("GameBoosterService", $"Games installed: {found}");
            GamesInstalledChanged?.Invoke(found);
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
        var procs = Array.Empty<Process>();
        try
        {
            procs = Process.GetProcesses();
            foreach (var proc in procs)
            {
                try { if (IsAntiCheatProcess(proc.ProcessName)) return true; }
                catch { }
            }
        }
        catch { }
        finally
        {
            foreach (var proc in procs) { try { proc.Dispose(); } catch { } }
        }
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
            GameMatch? detected = FindRunningGame();

            // SESSION STICKINESS. Detection answers "is a game on screen right now", which is the
            // right question for STARTING a boost and the wrong one for ending it: a fullscreen
            // game minimises the moment you alt-tab, so the honest answer becomes "no game" while
            // the user is very much still playing. Boost would drop, then come back when they
            // tabbed in — churning the power plan, Wi-Fi and battery pause every time.
            //
            // Once a boost is running for a real, named game, the only thing that ends it is that
            // game exiting. The anti-cheat tier in FindRunningGame already covers the alt-tab case
            // for protected titles; this covers everything else.
            if (detected == null && _boostActive && _activeMatch is { IsKnownGame: true } active
                && IsProcessAlive(active.Pid))
                detected = active;

            string? detectedGame = detected?.Name;

            // Events and tray calls are captured as actions and fired OUTSIDE the lock
            // to prevent deadlocks if UI event handlers call back into this service.
            Action? postLockAction = null;
            lock (_lock)
            {
                // Re-arm the moment the session the user opted out of is over: a different game,
                // or no game at all, means the suppression no longer applies.
                if (_autoBoostSuppressedFor != null &&
                    !string.Equals(detectedGame, _autoBoostSuppressedFor, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Info("GameBoosterService",
                        $"Auto-boost re-armed — '{_autoBoostSuppressedFor}' session ended");
                    _autoBoostSuppressedFor = null;
                }

                bool suppressed = detectedGame != null &&
                    string.Equals(detectedGame, _autoBoostSuppressedFor, StringComparison.OrdinalIgnoreCase);

                bool shouldActivate = detectedGame != null && !_boostActive && !suppressed;
                // Never auto-deactivate while manual boost is on — user controls it explicitly
                bool shouldDeactivate = detectedGame == null && _boostActive && !_manualBoostActive;

                if (shouldActivate)
                    postLockAction = ActivateBoost(detected!.Value);
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

    /// <summary>
    /// True when a process name alone identifies a game. Shared by the fast foreground check and
    /// the full pass so the two can never disagree about what counts as a game.
    /// </summary>
    private static bool IsKnownGameProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (KnownGameProcesses.Contains(processName)) return true;

        foreach (var suffix in KnownGameSuffixes)
            if (processName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// True when this process name could be a game — either a confirmed game name or one that
    /// needs its window title checked.
    ///
    /// Exposed so Launch Boost can leave games alone. The two features were both boosting the same
    /// process and it did not merely duplicate work: Launch Boost fires the instant a process
    /// starts, sets CPU High and GPU Realtime, and records Normal as the value to restore. Game
    /// Booster claims the process a few seconds later and captures the ALREADY-BOOSTED state as
    /// its own "original", so when the session ended it put the game back to High/Realtime and
    /// left it there for the life of the process. Games belong to Game Booster; Launch Boost keeps
    /// everything else.
    /// </summary>
    internal static bool IsPotentialGameProcess(string processName) =>
        IsKnownGameProcess(processName) || IsTitleQualifiedCandidate(processName);

    /// <summary>True when the name is one that needs its window title checked (see TitleQualifiedGames).</summary>
    private static bool IsTitleQualifiedCandidate(string processName)
    {
        foreach (var (name, _) in TitleQualifiedGames)
            if (processName.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Confirms a title-qualified candidate by reading its main window title.</summary>
    private static bool IsTitleQualifiedGame(Process proc)
    {
        foreach (var (name, titleFragment) in TitleQualifiedGames)
        {
            if (!proc.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            try { return proc.MainWindowTitle.Contains(titleFragment, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        return false;
    }

    /// <summary>Cheap "is this PID still around?" used to keep a session alive across alt-tab.</summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>True when the process name looks like a known anti-cheat (substring, so
    /// "EasyAntiCheat_EOS" matches "EasyAntiCheat").</summary>
    private static bool IsAntiCheatProcess(string processName)
    {
        foreach (var ac in AntiCheatProcesses)
            if (processName.Contains(ac, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Cheap foreground check, run every few seconds. The full CheckRunningGames pass calls
    /// Process.GetProcesses() and cannot run at this rate, so it stayed on a 2-minute timer —
    /// which meant boost could arrive two minutes into a match and linger two minutes after it.
    ///
    /// This costs one window handle, one PID and one process name, and only does anything when the
    /// FOREGROUND APP CHANGES, which is the natural trigger for both "a game came up" and "the game
    /// went away". When it sees a transition worth acting on it hands off to the real pass, so all
    /// activation/deactivation logic (and its locking) stays in exactly one place.
    /// </summary>
    private void FastForegroundGameCheck()
    {
        if (!_settings.GameBoosterEnabled) return;

        try
        {
            var (fgPid, fgName) = GetForegroundProcess();
            if (fgPid == 0) return;

            if (string.Equals(fgName, _lastForegroundName, StringComparison.OrdinalIgnoreCase))
                return;                                          // nothing changed — stay cheap
            _lastForegroundName = fgName;

            // Title-qualified names (javaw) count as "maybe" here; the full pass reads the window
            // title to decide. Being generous costs one extra full pass, being strict would mean
            // Minecraft waits out the slow timer.
            bool fgIsGame = IsKnownGameProcess(fgName) || IsTitleQualifiedCandidate(fgName);

            // A game just came to the front and we're not boosting → confirm on the full pass.
            // Not boosting anything here directly: the suppression list, anti-cheat detection and
            // on-screen checks all live in CheckRunningGames and must not be duplicated.
            if (!_boostActive && fgIsGame)
            {
                _ = RunOnLargeStackAsync(CheckRunningGames);
                return;
            }

            // We're boosting and the user moved to something that isn't the game → let the full
            // pass decide whether the game actually exited (alt-tabbing must NOT drop the boost).
            if (_boostActive && !fgIsGame)
                _ = RunOnLargeStackAsync(CheckRunningGames);
        }
        catch { /* detection must never throw into the timer */ }
    }

    /// <summary>
    /// Answers one question: is a game on screen right now, and what is it called?
    ///
    /// The ordering is deliberate:
    ///   1. the FOREGROUND process, when it is a game — what the user is actually playing
    ///   2. any other on-screen game — a game on a second monitor, or a launcher in front of it
    ///   3. anti-cheat running plus a real app in front — a game we can't name but can still boost
    ///
    /// This used to be a single loop that returned whichever process Windows happened to enumerate
    /// first, with the anti-cheat check nested INSIDE that loop. With Vanguard, EAC or BattlEye
    /// installed, the anti-cheat service usually came first, so a perfectly recognisable game got
    /// reported as "Unknown Game" — and because ActivateBoost decided whether to raise the game's
    /// priority by substring-matching that display name, the main thing the boost does was silently
    /// skipped. <see cref="GameMatch"/> now carries that fact explicitly instead of encoding it in
    /// prose. The anti-cheat scan is also no longer re-run (with a foreground lookup) per process.
    /// </summary>
    private static GameMatch? FindRunningGame()
    {
        try
        {
            // Only boost a game that's actually ON SCREEN — a minimized game shouldn't trigger
            // (or hold) boost. Uses the same EnumWindows + IsIconic / IsWindowVisible detection
            // Task Sleep already ships, so no new API surface is introduced.
            var onScreenPids  = GetOnScreenPids();
            var (fgPid, fgName) = GetForegroundProcess();

            GameMatch? onScreenGame  = null;
            GameMatch? offScreenGame = null;
            bool antiCheatRunning    = false;

            var procs = Process.GetProcesses();
            try
            {
                foreach (var proc in procs)
                {
                    try
                    {
                        // Anti-cheat can be a background service with no window, so this is checked
                        // for every process — but only as a fallback, and only once each.
                        if (!antiCheatRunning && IsAntiCheatProcess(proc.ProcessName))
                            antiCheatRunning = true;

                        bool isGame = IsKnownGameProcess(proc.ProcessName) || IsTitleQualifiedGame(proc);

                        if (!onScreenPids.Contains(proc.Id))
                        {
                            // Remember a game that is running but not currently on screen. A
                            // fullscreen game MINIMISES when you alt-tab, so this is the normal
                            // state whenever the user looks at anything else — including Systema.
                            if (isGame) offScreenGame ??= new GameMatch(proc.ProcessName, IsKnownGame: true, Pid: proc.Id);
                            continue;
                        }
                        if (!isGame) continue;

                        // The game the user is looking at wins outright — stop here.
                        if (proc.Id == fgPid)
                            return new GameMatch(proc.ProcessName, IsKnownGame: true, Pid: proc.Id);

                        // Otherwise remember it and keep looking for a foreground match.
                        onScreenGame ??= new GameMatch(proc.ProcessName, IsKnownGame: true, Pid: proc.Id);
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (var proc in procs) { try { proc.Dispose(); } catch { } }
            }

            if (onScreenGame != null) return onScreenGame;

            // Nothing on screen, but a game we RECOGNISE is running and its anti-cheat is loaded.
            // That is a live session — a fullscreen game that minimised when the user alt-tabbed.
            // Name it. Reporting "Unknown Game" here was the bug: the placeholder carries
            // IsKnownGame: false, which skips the priority boost entirely, so Fortnite got detected
            // and then deliberately left alone.
            if (antiCheatRunning && offScreenGame != null)
                return offScreenGame;

            // Anti-cheat running but nothing we recognise anywhere. Still a game session worth
            // boosting the system for, we just can't name the process — so no per-process boost.
            // Gated on the foreground not being the shell so it won't fire on an empty desktop.
            if (antiCheatRunning && fgPid != 0 &&
                !fgName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                return new GameMatch(UnknownGameName, IsKnownGame: false);
        }
        catch { }
        return null;
    }

    // ── On-screen / minimized detection ────────────────────────────────────────
    // Same window-state method Task Sleep uses (EnumWindows + IsWindowVisible + IsIconic) —
    // these are standard, ubiquitous user32 calls already present in the binary, so adding
    // them here introduces no new API surface.
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    /// <summary>PIDs that own at least one visible, non-minimized top-level window — i.e. apps
    /// actually on screen. Same EnumWindows + IsWindowVisible + IsIconic approach as Task Sleep.</summary>
    private static HashSet<int> GetOnScreenPids()
    {
        var pids = new HashSet<int>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (IsWindowVisible(hWnd) && !IsIconic(hWnd))
                    {
                        GetWindowThreadProcessId(hWnd, out uint wpid);
                        if (wpid != 0) pids.Add((int)wpid);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"GetOnScreenPids failed: {ex.Message}"); }
        return pids;
    }

    /// <summary>
    /// The PID and process name of whatever owns the foreground window, or (0, "") if that can't
    /// be determined. One helper for what used to be three near-identical copies of this same
    /// GetForegroundWindow → GetWindowThreadProcessId → GetProcessById dance.
    /// The old version also leaked a Process handle per call by never disposing it.
    /// </summary>
    private static (int Pid, string Name) GetForegroundProcess()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return (0, "");
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return (0, "");
            using var proc = Process.GetProcessById((int)pid);
            return ((int)pid, proc.ProcessName);
        }
        catch { return (0, ""); }
    }

    // ── Boost Activation ──────────────────────────────────────────────────────

    /// <summary>
    /// Activates game boost. Must be called inside <see cref="_lock"/>.
    /// Returns an <see cref="Action"/> containing UI event and tray notifications that must be
    /// invoked AFTER releasing the lock to prevent deadlocks with UI event handlers.
    /// </summary>
    private Action? ActivateBoost(GameMatch match)
    {
        // Applying a boost is the busiest second of Systema's life and it lands while a game is
        // launching. Ghost Mode has us at Idle priority, which stretched this to nine seconds and
        // starved the UI heartbeat into a false crash report. Take Normal priority for the duration.
        using var priority = _tray?.BorrowNormalPriority();

        string gameName = match.Name;
        _activeMatch     = match;
        _boostedRealGame = match.IsKnownGame;
        _boostActive   = true;
        // Announce the session before applying anything, so other features stand down for its
        // whole duration rather than from whenever the first setting happened to land.
        BoostedGameRegistry.SessionActive = true;
        BoostStartedAt = DateTime.UtcNow;
        ActiveGameName = gameName;
        _killedServices.Clear();

        _log.Info("GameBoosterService", $"Boost activated for: {gameName}");

        // Service pausing removed (2026-06): modern Windows handles game prioritisation
        // (Game Mode + the scheduler), so stopping background services during boost is
        // unnecessary and disruptive. The boost now only adjusts priority, GPU, and power.
        // _killedServices stays empty; the restore/recovery paths below are kept so any
        // services paused by an OLDER Systema build still get restored on upgrade.
        CrashGuard.Mark($"Game Boost active for {gameName} (background services are no longer paused)");

        // NO PER-PROCESS PRIORITY CHANGES. Systema deliberately does not touch the game process
        // itself — not CPU priority, not I/O, not GPU, not memory, not power throttling.
        //
        // v0.7.281 tried it and it was actively harmful: anti-cheat treats external manipulation
        // of a protected game as tampering. Fortnite (EAC), BeamNG and Roblox all force-closed.
        // That is by design on their side, not a bug we can work around, and no amount of boost is
        // worth ending the player's session. Everything a boost does now is SYSTEM-level (power
        // plan, indexing, network, notifications) and leaves the game alone.
        //
        // Process Lasso's ProBalance exclusion stays: it changes Lasso's own configuration, not
        // the game process.
        if (match.IsKnownGame)
        {
            _boostedProcessName = $"{gameName}.exe";
            if (_processLasso.IsInstalled())
                _processLasso.ExcludeFromProBalance(_boostedProcessName);
        }

        // WAL (write-ahead log) pattern: read all pre-boost originals into _saved* fields
        // BEFORE making any system changes, then persist them to disk immediately.
        // If the PC crashes or power-cuts anywhere during ApplyBoostOptions, the next
        // Systema startup finds boost_state.json and fully restores all settings — even
        // if the crash happened on the very first Apply call.
        ReadBoostOriginals();
        PersistBoostState();

        // Apply new boost options (memory, notifications, power plan)
        ApplyBoostOptions(gameName);

        // All risky operations complete — clear the crash sentinel so a normal GC pause
        // during the boost session doesn't trigger a false crash report.
        CrashGuard.Clear();

        // Return UI/tray notifications as an action to fire outside the lock.
        // Firing events inside a lock risks deadlock if any UI handler calls back into this service.
        var capturedGameName = gameName;
        return () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => BoostActivated?.Invoke(capturedGameName));
            else
                BoostActivated?.Invoke(capturedGameName);
            _tray?.SetTooltip($"Systema — Boosting: {capturedGameName}");
            _tray?.ShowBalloon("Game Boost Active",
                // Service pausing was removed in June 2026; the old wording claimed something
                // that no longer happens.
                $"Boosting {capturedGameName}. Everything goes back to normal when you quit.",
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
        // Same reasoning as ActivateBoost: restoring the power plan, the Dell charge thresholds
        // and the network settings is real work that shouldn't crawl at Idle priority.
        using var priority = _tray?.BorrowNormalPriority();

        // Nothing to restore on the game process — Systema never changed it. See ActivateBoost.
        if (_boostedProcessName != null && _processLasso.IsInstalled())
        {
            _processLasso.RemoveProBalanceExclusion(_boostedProcessName);
            _boostedProcessName = null;
        }

        _boostedRealGame = false;
        _activeMatch   = null;
        _boostActive   = false;
        BoostedGameRegistry.SessionActive = false;
        ActiveGameName = null;
        BoostStartedAt = null;

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
                if (!ex.Message.Contains("was not found"))
                    _log.Warn("GameBoosterService", $"Could not restore {svcName}: {ex.Message}");
            }
        }
        _killedServices.Clear();

        // Clean deactivation — delete the persisted snapshot so next startup
        // doesn't try to restore again.
        ClearPersistedBoostState();

        CrashGuard.Clear();

        // Return UI/tray notifications as an action to fire outside the lock.
        return () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(() => BoostDeactivated?.Invoke());
            else
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

    /// <summary>
    /// Raises everything Windows lets us raise on the game itself.
    /// <paramref name="pid"/> is the process detection actually matched; it is used in preference
    /// to a name lookup, which both over-reaches (every javaw on the machine, IDE included) and
    /// can come back empty and leave a detected game unboosted.
    /// </summary>
    // ── New Boost Options ─────────────────────────────────────────────────────

    private void ApplyBoostOptions(string gameName)
    {
        // –1. Prevent system sleep — applied first so it covers the entire boost window,
        //     including any early exit if the game crashes right after launch.
        if (_settings.GameBoosterPreventSleep) ApplyPreventSleep();

        // –0.5. Pause battery charging on supported laptops. Vendor BIOS hook (Dell /
        //       Lenovo today). Snapshot the original mode so we can restore on a clean
        //       deactivation OR on next launch after a crash. Cheap WMI calls — runs
        //       inline. If the user opted in but the device isn't supported, this is a
        //       no-op.
        if (_settings.GameBoosterPauseCharging) ApplyBatteryPause();

        // 0. New network / system options (applied before the heavy RAM trim)
        if (_settings.GameBoosterDisableGameBar)   ApplyGameBarDisable();
        if (_settings.GameBoosterGpuProfile)       ApplyMultimediaProfile();
        if (_settings.GameBoosterPauseIndexing)    PauseIndexing();
        if (_settings.GameBoosterDisableNagle)          ApplyDisableNagle();
        if (_settings.GameBoosterFlushDns)              FlushDns();
        if (_settings.GameBoosterNicPowerSaving)        ApplyNicPowerSaving();
        // WiFi disable uses NetworkInterface and WLAN API — both can trigger network driver
        // callbacks that block the calling thread for several seconds. Fire on a threadpool
        // thread so the DispatcherTimer tick (or lock-holding background thread) is not stalled.
        if (_settings.GameBoosterDisableWifiOnEthernet)
            _ = System.Threading.Tasks.Task.Run(ApplyDisableWifi);
        // Bluetooth uses SetupAPI device enable/disable — can briefly stall while the
        // driver stack unloads, so fire on a threadpool thread like Wi-Fi.
        if (_settings.GameBoosterDisableBluetooth)
            _ = System.Threading.Tasks.Task.Run(ApplyDisableBluetooth);

        // 1. Aggressively trim RAM from background processes:
        //    Step 1 — per-process: remove working-set floor then flush pages to standby list.
        //    Step 2 — system-wide: flush modified pages, then purge the standby list so all
        //             those pages become immediately free (not just potentially reusable).
        //
        // VSYNC: we EXCLUDE dwm.exe, audiodg.exe, svchost.exe, and GPU vendor user-mode
        // processes. Trimming dwm's working set forces the compositor to page from disk
        // and breaks NVIDIA MPO / Independent Flip for the whole desktop — causing the
        // very tearing this Game Booster is trying to prevent. The list mirrors
        // MemoryService.VsyncCriticalProcessNames; keep them in sync.
        if (_settings.GameBoosterFreeMemory)
        {
            try
            {
                // Before/after measurement — without this we previously logged
                // "Trimmed N processes" but had no idea whether anything was
                // actually freed. Matches MemoryService.FreeRam's reporting so
                // diagnostic logs are directly comparable.
                long beforeAvailMb = GetAvailableRamMb();

                var procs = Process.GetProcesses();
                int trimmed = 0, skipped = 0;
                foreach (var proc in procs)
                {
                    try
                    {
                        if (proc.ProcessName.Equals(gameName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (proc.Id <= 4) continue;
                        if (VsyncCriticalProcessNames.Contains(proc.ProcessName))
                        {
                            skipped++;
                            continue;
                        }
                        // Never trim anti-cheat processes — kernel-mode AC drivers
                        // (Vanguard, EAC, BattlEye, etc.) can intercept OpenProcess /
                        // EmptyWorkingSet on their handles and react with an unrecoverable
                        // AccessViolationException that terminates the whole process, or
                        // flag Systema as a threat and cause a game ban.
                        if (Array.Exists(AntiCheatProcesses, ac =>
                            proc.ProcessName.Contains(ac, StringComparison.OrdinalIgnoreCase)))
                        {
                            skipped++;
                            continue;
                        }
                        // Open with the rights EmptyWorkingSet + SetProcessWorkingSetSize
                        // BOTH require: PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA.
                        // The old code used PROCESS_SET_INFORMATION alone, which made
                        // every EmptyWorkingSet call return false with ERROR_ACCESS_DENIED
                        // — the loop counter still incremented and the log claimed
                        // success, but Task Manager showed zero RAM actually freed.
                        IntPtr h = OpenProcess(PROCESS_TRIM_WORKING_SET, false, proc.Id);
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

                // Brief pause so Windows reclaims the trimmed pages before we re-sample,
                // matching MemoryService.FreeRam (which sleeps 500ms for the same reason).
                System.Threading.Thread.Sleep(500);
                long afterAvailMb = GetAvailableRamMb();
                long freedMb      = Math.Max(0, afterAvailMb - beforeAvailMb);

                _log.Info("GameBoosterService",
                    $"Freed ~{freedMb:N0} MB by trimming {trimmed} background processes for {gameName} (skipped {skipped} VSync-critical)");
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

        // 4. Search-indexing pause removed (2026-06): Systema no longer stops the WSearch
        // service during boost — not needed on modern Windows. The restore path below is
        // kept so a session paused by an older build still gets WSearch back.
        _searchIndexingWasRunning = false;

        // 5. Switch to High Performance power plan
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
        // –1. Remove sleep prevention first — system is free to sleep again immediately
        //     once the boost ends, regardless of how long the other restores take.
        RestorePreventSleep();

        // –0.5. Resume battery charging if we paused it. Always best-effort; never
        //       throws even if the snapshot is null or the vendor hook is gone.
        RestoreBatteryPause();

        // 0. Restore new options (order: reverse of apply)
        RestoreBluetooth();
        RestoreWifi();
        RestoreNicPowerSaving();
        RestoreNagle();
        RestoreMultimediaProfile();
        ResumeIndexing();   // always best-effort — no-op if we didn't pause it
        if (_savedAppCaptureEnabled.HasValue || _savedGameDvrEnabled.HasValue) RestoreGameBarDvr();

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

        // 2. Restore Search Indexing — only if it was running before boost
        if (_searchIndexingWasRunning)
        {
            try
            {
                // Set back to Auto start
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\WSearch", true);
                key?.SetValue("Start", 2, RegistryValueKind.DWord);
                // Start the service
                using var svc = new ServiceController("WSearch");
                if (svc.Status == ServiceControllerStatus.Stopped)
                {
                    svc.Start();
                    try { svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8)); } catch { }
                }
                _log.Info("GameBoosterService", "Search indexing restored after boost");
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreSearchIndexing failed: {ex.Message}"); }
            finally { _searchIndexingWasRunning = false; }
        }

        // 3. Restore power plan
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

    // ── Crash-recovery: persist / clear / recover ─────────────────────────────

    /// <summary>
    /// Reads every pre-boost system value into the corresponding _saved* field WITHOUT
    /// modifying anything.  Called immediately before <see cref="PersistBoostState"/>
    /// so the on-disk snapshot is written before any system changes are made
    /// (write-ahead log pattern).  If the PC crashes mid-apply the snapshot already
    /// contains all originals and <see cref="RecoverBoostStateFromCrash"/> can restore them.
    /// </summary>
    private void ReadBoostOriginals()
    {
        // ── Game Bar / DVR ────────────────────────────────────────────────────
        if (_settings.GameBoosterDisableGameBar)
        {
            try
            {
                using var dvrKey = Registry.CurrentUser.OpenSubKey(GameDvrKey);
                if (dvrKey != null)
                {
                    var cur = dvrKey.GetValue("AppCaptureEnabled");
                    _savedAppCaptureEnabled = cur is int i ? i : 1;
                }
            }
            catch { }
            try
            {
                using var cfgKey = Registry.CurrentUser.OpenSubKey(GameConfigKey);
                if (cfgKey != null)
                {
                    var cur = cfgKey.GetValue("GameDVR_Enabled");
                    _savedGameDvrEnabled = cur is int i ? i : 1;
                }
            }
            catch { }
        }

        // ── Multimedia System Profile (SystemResponsiveness + Games sub-key) ──
        if (_settings.GameBoosterGpuProfile)
        {
            try
            {
                using var profKey = Registry.LocalMachine.OpenSubKey(MmProfileKey);
                if (profKey != null)
                {
                    var cur = profKey.GetValue("SystemResponsiveness");
                    _savedSystemResponsiveness = cur is int i ? i : 20;
                }
            }
            catch { }
            try
            {
                using var gamesKey = Registry.LocalMachine.OpenSubKey(MmGamesKey);
                if (gamesKey != null)
                {
                    var curPri           = gamesKey.GetValue("Priority");
                    _savedMmPriority         = curPri is int ip ? ip : 2;
                    _savedSchedulingCategory = gamesKey.GetValue("Scheduling Category") as string ?? "Medium";
                    _savedSfIoPriority       = gamesKey.GetValue("SFIO Priority")       as string ?? "Normal";
                }
            }
            catch { }
        }

        // ── Nagle — read per-adapter TCP values without writing ───────────────
        if (_settings.GameBoosterDisableNagle)
        {
            var restore = new List<(string, string, object?)>();
            try
            {
                using var ifacesKey = Registry.LocalMachine.OpenSubKey(TcpipIfacesKey);
                if (ifacesKey != null)
                {
                    foreach (var guid in ifacesKey.GetSubKeyNames())
                    {
                        var path = $@"{TcpipIfacesKey}\{guid}";
                        try
                        {
                            using var iKey = Registry.LocalMachine.OpenSubKey(path);
                            if (iKey == null) continue;
                            var savedAck   = iKey.GetValue("TcpAckFrequency");
                            var savedDelay = iKey.GetValue("TCPNoDelay");
                            restore.Add((path, "TcpAckFrequency", savedAck));
                            restore.Add((path, "TCPNoDelay",      savedDelay));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _nagleRestore = restore;
        }

        // ── NIC power saving — read per-adapter PnPCapabilities without writing
        if (_settings.GameBoosterNicPowerSaving)
        {
            var restore = new List<(string, string, object?)>();
            try
            {
                using var nicClass = Registry.LocalMachine.OpenSubKey(NicClassKey);
                if (nicClass != null)
                {
                    foreach (var subName in nicClass.GetSubKeyNames())
                    {
                        if (!int.TryParse(subName, out _)) continue;
                        var path = $@"{NicClassKey}\{subName}";
                        try
                        {
                            using var adapterKey = Registry.LocalMachine.OpenSubKey(path);
                            if (adapterKey == null) continue;
                            if (adapterKey.GetValue("NetCfgInstanceId") == null) continue;
                            var savedVal = adapterKey.GetValue("PnPCapabilities");
                            restore.Add((path, "PnPCapabilities", savedVal));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _nicPowerRestore = restore;
        }

        // ── Notifications ─────────────────────────────────────────────────────
        if (_settings.GameBoosterSuppressNotifications)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(NotificationKey);
                // If key absent, Windows default is notifications ON — treat as 1
                var cur = key?.GetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED") as int?;
                bool alreadyOff = cur.HasValue && cur.Value == 0;
                _savedNotificationsEnabled = alreadyOff ? (int?)null : (cur ?? 1);
            }
            catch { }
        }

        // ── Windows Search service ────────────────────────────────────────────
        // No longer tracked — Systema doesn't stop WSearch during boost, so there's
        // nothing to restore. (Recovery from an older build's snapshot still works.)
        _searchIndexingWasRunning = false;

        // ── Active power plan GUID ────────────────────────────────────────────
        if (_settings.GameBoosterHighPerfPowerPlan)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var ps = System.Diagnostics.Process.Start(psi);
                if (ps != null)
                {
                    string output = ps.StandardOutput.ReadToEnd();
                    ps.WaitForExit(3000);
                    var match = System.Text.RegularExpressions.Regex.Match(
                        output,
                        @"GUID:\s+([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})");
                    if (match.Success) _savedPowerPlanGuid = match.Groups[1].Value;
                }
            }
            catch { }
        }

        // WiFi / Bluetooth: no pre-read needed.  _wifiRadioDisabled and
        // _bluetoothRadioDisabled default to false and are only set to true by
        // the async Apply methods after they actually disable the radio.
        // At persist-time they are correctly false (nothing disabled yet).

        // ── Battery pause: capture the vendor mode BEFORE any WMI write so the
        //    snapshot persisted to disk contains the original. We set WasPaused=true
        //    as an "intent to restore" flag — even if the actual WMI write later
        //    fails or crashes mid-call, RecoverBoostStateFromCrash will set the
        //    vendor mode back to OriginalMode on next launch. Restoring to the
        //    same value is a no-op if pause never actually happened — safe.
        if (_settings.GameBoosterPauseCharging)
        {
            try
            {
                var support = _batteryPause.DetectSupport();
                if (support == BatteryPauseSupport.Supported)
                {
                    _batteryPauseSnapshot = new BatteryPauseSnapshot
                    {
                        Method       = _batteryPause.ActiveMethodName,
                        Vendor       = _batteryPause.Vendor,
                        OriginalMode = _batteryPause.GetCurrentVendorMode(),
                        WasPaused    = true,
                    };
                }
            }
            catch (Exception ex) { _log.Warn("GameBoosterService", $"BatteryPause pre-read: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Writes all pre-boost original values to disk so they survive a crash.
    /// Called immediately after <see cref="ReadBoostOriginals"/> and BEFORE
    /// <see cref="ApplyBoostOptions"/> — write-ahead log pattern.
    /// </summary>
    private void PersistBoostState()
    {
        try
        {
            var snapshot = new BoostStateSnapshot
            {
                GameName                 = ActiveGameName,
                KilledServices           = new List<string>(_killedServices),
                NotificationsEnabled     = _savedNotificationsEnabled,
                PowerPlanGuid            = _savedPowerPlanGuid,
                SearchIndexingWasRunning = _searchIndexingWasRunning,
                AppCaptureEnabled        = _savedAppCaptureEnabled,
                GameDvrEnabled           = _savedGameDvrEnabled,
                SystemResponsiveness     = _savedSystemResponsiveness,
                MmPriority               = _savedMmPriority,
                SchedulingCategory       = _savedSchedulingCategory,
                SfIoPriority             = _savedSfIoPriority,
                WifiRadioDisabled        = _wifiRadioDisabled,
                BluetoothRadioDisabled   = _bluetoothRadioDisabled,
                BatteryPauseMethod       = _batteryPauseSnapshot?.Method,
                BatteryPauseVendor       = _batteryPauseSnapshot?.Vendor,
                BatteryPauseOriginalMode = _batteryPauseSnapshot?.OriginalMode,
                BatteryPauseWasActive    = _batteryPauseSnapshot?.WasPaused == true,
            };

            if (_nagleRestore != null)
                snapshot.NagleRestore = _nagleRestore
                    .Select(r => new RegistryRestoreEntry { Path = r.path, Name = r.name, Val = r.val is int i ? i : null })
                    .ToList();

            if (_nicPowerRestore != null)
                snapshot.NicPowerRestore = _nicPowerRestore
                    .Select(r => new RegistryRestoreEntry { Path = r.path, Name = r.name, Val = r.val is int i ? i : null })
                    .ToList();

            Directory.CreateDirectory(BoostStateDir);
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            // Atomic write: temp file then rename to prevent corruption on crash mid-write
            var tmp = BoostStatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, BoostStatePath, overwrite: true);
            _log.Info("GameBoosterService", "Boost state persisted to disk for crash recovery");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"PersistBoostState failed: {ex.Message}"); }
    }

    /// <summary>
    /// Deletes the persisted boost state file — called after a clean deactivation.
    /// </summary>
    private void ClearPersistedBoostState()
    {
        try { if (File.Exists(BoostStatePath)) File.Delete(BoostStatePath); }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ClearPersistedBoostState failed: {ex.Message}"); }
    }

    /// <summary>
    /// Called once at startup. If a boost_state.json file exists, the previous session
    /// crashed mid-boost. Load the saved originals and run the normal restore logic so
    /// services, registry, power plan, etc. are returned to their pre-boost values.
    /// </summary>
    private void RecoverBoostStateFromCrash()
    {
        try
        {
            if (!File.Exists(BoostStatePath)) return;

            var json = File.ReadAllText(BoostStatePath);
            var snap = JsonSerializer.Deserialize<BoostStateSnapshot>(json);
            if (snap == null)
            {
                ClearPersistedBoostState();
                return;
            }

            _log.Warn("GameBoosterService",
                $"Crash recovery: previous boost for '{snap.GameName}' was active when Systema exited — restoring original settings now");

            // Load saved originals into the in-memory fields
            _killedServices.Clear();
            if (snap.KilledServices != null) _killedServices.AddRange(snap.KilledServices);
            _savedNotificationsEnabled  = snap.NotificationsEnabled;
            _savedPowerPlanGuid         = snap.PowerPlanGuid;
            _searchIndexingWasRunning   = snap.SearchIndexingWasRunning;
            _savedAppCaptureEnabled     = snap.AppCaptureEnabled;
            _savedGameDvrEnabled        = snap.GameDvrEnabled;
            _savedSystemResponsiveness  = snap.SystemResponsiveness;
            _savedMmPriority            = snap.MmPriority;
            _savedSchedulingCategory    = snap.SchedulingCategory;
            _savedSfIoPriority          = snap.SfIoPriority;
            _wifiRadioDisabled          = snap.WifiRadioDisabled;
            _bluetoothRadioDisabled     = snap.BluetoothRadioDisabled;
            _batteryPauseSnapshot       = snap.BatteryPauseWasActive
                ? new BatteryPauseSnapshot
                {
                    Method       = snap.BatteryPauseMethod,
                    Vendor       = snap.BatteryPauseVendor,
                    OriginalMode = snap.BatteryPauseOriginalMode,
                    WasPaused    = true,
                }
                : null;

            if (snap.NagleRestore != null)
                _nagleRestore = snap.NagleRestore
                    .Select(r => (r.Path, r.Name, (object?)(r.Val.HasValue ? r.Val.Value : null)))
                    .ToList();

            if (snap.NicPowerRestore != null)
                _nicPowerRestore = snap.NicPowerRestore
                    .Select(r => (r.Path, r.Name, (object?)(r.Val.HasValue ? r.Val.Value : null)))
                    .ToList();

            // Run normal restore — RestoreBoostOptions handles all registry/settings,
            // then restore killed services
            RestoreBoostOptions();

            foreach (var svcName in _killedServices)
            {
                try
                {
                    using var svc = new ServiceController(svcName);
                    svc.Refresh();
                    if (svc.Status == ServiceControllerStatus.Stopped)
                    {
                        svc.Start();
                        _log.Info("GameBoosterService", $"Crash recovery: restored service {svcName}");
                    }
                }
                catch (Exception ex)
                {
                    if (!ex.Message.Contains("was not found"))
                        _log.Warn("GameBoosterService", $"Crash recovery: could not restore {svcName}: {ex.Message}");
                }
            }
            _killedServices.Clear();

            ClearPersistedBoostState();
            _log.Info("GameBoosterService", "Crash recovery complete — all boost settings restored");
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"RecoverBoostStateFromCrash failed: {ex.Message}");
            // Delete corrupt file so it doesn't block every startup
            ClearPersistedBoostState();
        }
    }

    // ── New Boost Helpers ─────────────────────────────────────────────────────

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

    /// <summary>
    /// Legacy startup check for SystemResponsiveness. Older Systema builds wrote
    /// SystemResponsiveness=0 as a "gamer tweak" which, on older Windows builds, starved
    /// MMCSS priority boost for DWM and broke NVIDIA MPO / Independent Flip.
    /// <para>
    /// This NO LONGER force-heals the value. Per the user (2026-06-11) newer Windows
    /// builds no longer regress, and 0 is now an explicit, opt-in System Tweak
    /// (System Tweaks → Maximum System Responsiveness). So when we find an existing 0 we
    /// ADOPT it as the user's preference instead of overwriting it — honouring the
    /// "reflect the current Windows value, never change it on launch" requirement. The
    /// user restores the Windows default (20) any time from that toggle.
    /// </para>
    /// </summary>
    private void RepairVSyncCriticalRegistryValues()
    {
        try
        {
            using var profKey = Registry.LocalMachine.OpenSubKey(MmProfileKey, writable: false);
            if (profKey == null) return;

            var cur = profKey.GetValue("SystemResponsiveness");
            if (cur is int i && i == 0 && !_settings.MaxResponsivenessEnabled)
            {
                // Adopt the existing 0 as the user's choice; stop auto-healing it.
                _settings.MaxResponsivenessEnabled = true;
                _log.Info("GameBoosterService",
                    "SystemResponsiveness=0 detected — adopted as user preference; System Tweaks now owns this value (no longer auto-healed to 20)");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"RepairVSyncCriticalRegistryValues: {ex.Message}");
        }
    }

    private void ApplyMultimediaProfile()
    {
        try
        {
            // VSYNC WARNING: SystemResponsiveness is deliberately NOT modified.
            // Setting HKLM\...\Multimedia\SystemProfile\SystemResponsiveness to 0
            // (a popular "gamer tweak") gives games 100 % of CPU quanta but
            // STARVES MMCSS's priority boost for the DWM render/compositor threads.
            // DWM misses its 60/144/240 Hz presentation deadlines and NVIDIA MPO /
            // Independent Flip falls back to composed mode, causing hard tearing on
            // every window — including the foreground game. The Windows default
            // (20) gives DWM the non-multimedia headroom it needs to keep flip
            // queues consistent. Leave it alone.
            _savedSystemResponsiveness = null;

            // Games task sub-key — raise scheduling priority and GPU/SFIO priority
            // for the MMCSS "Games" category specifically. This only affects threads
            // that opt in via AvSetMmThreadCharacteristics("Games", ...) — it does
            // NOT displace DWM (which uses the "Window Manager" category), so it is
            // VSync-safe.
            // CreateSubKey (not OpenSubKey) — the Tasks\Games key doesn't exist on every system,
            // and OpenSubKey returned null there, silently skipping the whole tweak ("doesn't set").
            using var gamesKey = Registry.LocalMachine.CreateSubKey(MmGamesKey, writable: true);
            if (gamesKey != null)
            {
                // Capture the TRUE originals ONCE and PERSIST them, so restore works even after a
                // crash/restart and a second apply never mistakes the boosted values for originals.
                // -1 / "" mean the value did not exist before boost → delete it on restore.
                if (!_settings.MmProfileSavedActive)
                {
                    _settings.MmProfileSavedPriority      = gamesKey.GetValue("Priority") is int ip ? ip : -1;
                    _settings.MmProfileSavedSchedCategory = gamesKey.GetValue("Scheduling Category") as string ?? "";
                    _settings.MmProfileSavedSfioPriority  = gamesKey.GetValue("SFIO Priority")       as string ?? "";
                    _settings.MmProfileSavedActive        = true;
                }

                gamesKey.SetValue("Priority",            6,      RegistryValueKind.DWord);
                gamesKey.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                gamesKey.SetValue("SFIO Priority",       "High", RegistryValueKind.String);
            }

            _log.Info("GameBoosterService", "Multimedia system profile tuned for gaming (SystemResponsiveness preserved — VSync safety)");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyMultimediaProfile: {ex.Message}"); }
    }

    private void RestoreMultimediaProfile()
    {
        try
        {
            // Legacy VSync heal: older builds set SystemResponsiveness=0 (which starved
            // MMCSS priority boost for DWM on older Windows — see ApplyMultimediaProfile).
            // We only normalize a stray 0 back to the Windows default (20) when the user
            // has NOT opted into Maximum System Responsiveness via System Tweaks. If they
            // have, their deliberate 0 is preserved — it's now a supported, user-owned
            // tweak, not something to clamp on every game-boost exit.
            if (!_settings.MaxResponsivenessEnabled)
            {
                try
                {
                    using var profKey = Registry.LocalMachine.OpenSubKey(MmProfileKey, writable: true);
                    if (profKey != null)
                    {
                        var cur = profKey.GetValue("SystemResponsiveness");
                        if (cur is int i && i == 0)
                        {
                            profKey.SetValue("SystemResponsiveness", 20, RegistryValueKind.DWord);
                            _log.Info("GameBoosterService", "SystemResponsiveness was 0 — normalized to 20 to restore MMCSS/DWM boost (VSync repair)");
                        }
                    }
                }
                catch { /* best-effort — never fail restore over this */ }
            }
            _savedSystemResponsiveness = null; // current code never sets this; kept for back-compat

            // Restore the MMCSS "Games" values from the PERSISTED originals, so it undoes
            // correctly even if Systema was killed mid-boost (the in-memory copy would be lost).
            if (_settings.MmProfileSavedActive)
            {
                using var gamesKey = Registry.LocalMachine.OpenSubKey(MmGamesKey, writable: true);
                if (gamesKey != null)
                {
                    if (_settings.MmProfileSavedPriority >= 0)
                        gamesKey.SetValue("Priority", _settings.MmProfileSavedPriority, RegistryValueKind.DWord);
                    else gamesKey.DeleteValue("Priority", throwOnMissingValue: false);

                    if (!string.IsNullOrEmpty(_settings.MmProfileSavedSchedCategory))
                        gamesKey.SetValue("Scheduling Category", _settings.MmProfileSavedSchedCategory, RegistryValueKind.String);
                    else gamesKey.DeleteValue("Scheduling Category", throwOnMissingValue: false);

                    if (!string.IsNullOrEmpty(_settings.MmProfileSavedSfioPriority))
                        gamesKey.SetValue("SFIO Priority", _settings.MmProfileSavedSfioPriority, RegistryValueKind.String);
                    else gamesKey.DeleteValue("SFIO Priority", throwOnMissingValue: false);
                }
                _settings.MmProfileSavedActive = false;
                _savedMmPriority = null;
                _savedSchedulingCategory = null;
                _savedSfIoPriority = null;
            }

            _log.Info("GameBoosterService", "Multimedia system profile restored");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreMultimediaProfile: {ex.Message}"); }
    }

    // ·· Pause / resume Windows Search indexing ································
    // Pauses indexing while a game is boosting by SUSPENDING the SearchIndexer process — the
    // WSearch service stays running (it's a pause, not a stop), so search keeps working and the
    // indexer simply halts crawling until resumed. Crash-safe: ResumeIndexing is also called on
    // startup, so a boost cut short by a crash never leaves the indexer suspended.
    [DllImport("ntdll.dll")] private static extern uint NtSuspendProcess(IntPtr hProcess);
    [DllImport("ntdll.dll")] private static extern uint NtResumeProcess(IntPtr hProcess);
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private bool _indexingPaused;

    private void PauseIndexing()
    {
        if (_indexingPaused) return;
        try
        {
            EnsureDebugPrivilege();   // SearchIndexer runs as SYSTEM — needed or the open is denied
            bool any = false;
            foreach (var p in Process.GetProcessesByName("SearchIndexer"))
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
                    if (h != IntPtr.Zero) { NtSuspendProcess(h); CloseHandle(h); any = true; }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (any) { _indexingPaused = true; _log.Info("GameBoosterService", "Search indexing paused (SearchIndexer suspended — WSearch service still running)"); }
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"PauseIndexing failed: {ex.Message}"); }
    }

    private void ResumeIndexing()
    {
        try
        {
            EnsureDebugPrivilege();   // also needed to re-open the SYSTEM-owned indexer to resume it
            foreach (var p in Process.GetProcessesByName("SearchIndexer"))
            {
                try
                {
                    IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, p.Id);
                    if (h != IntPtr.Zero) { NtResumeProcess(h); CloseHandle(h); }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (_indexingPaused) _log.Info("GameBoosterService", "Search indexing resumed");
            _indexingPaused = false;
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ResumeIndexing failed: {ex.Message}"); }
    }

    // ·· Sleep Prevention ······················································

    private void ApplyPreventSleep()
    {
        // ES_CONTINUOUS | ES_SYSTEM_REQUIRED: keep the machine awake for the duration
        // of the boost session. The monitor timer would normally let Windows decide when
        // to sleep; this overrides that for the game session only.
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        _sleepPrevented = true;
        _log.Info("GameBoosterService", "Sleep prevention active — system will not sleep while gaming");
    }

    private void RestorePreventSleep()
    {
        if (!_sleepPrevented) return;
        // ES_CONTINUOUS alone clears all previous SYSTEM_REQUIRED flags, restoring
        // whatever sleep timeout the user had configured in Power Options.
        SetThreadExecutionState(ES_CONTINUOUS);
        _sleepPrevented = false;
        _log.Info("GameBoosterService", "Sleep prevention cleared — normal sleep timeouts restored");
    }

    // ·· Battery Pause ·························································

    /// <summary>
    /// Asks the laptop's vendor BIOS hook (Dell / Lenovo) to pause battery
    /// charging while the boost is active. The pre-pause vendor mode is
    /// captured into _batteryPauseSnapshot so PersistBoostState can write
    /// it to disk for crash recovery.
    /// </summary>
    private void ApplyBatteryPause()
    {
        try
        {
            // Detect lazily — first call may probe WMI for ~50ms.
            var support = _batteryPause.DetectSupport();
            if (support != BatteryPauseSupport.Supported)
            {
                _log.Info("GameBoosterService",
                    $"Battery Pause skipped — device support state is {support}");
                return;
            }

            // User asked for "20 below current" as the floor; we hand it to the service
            // which honours it on Dell Custom mode, otherwise applies the vendor preset.
            int current   = _batteryPause.GetBatteryPercent() ?? 80;
            int threshold = Math.Max(30, current - 20);

            _batteryPauseSnapshot = _batteryPause.Pause(threshold);
            if (_batteryPauseSnapshot != null)
                _log.Info("GameBoosterService",
                    $"Battery charging paused via {_batteryPauseSnapshot.Vendor} (was '{_batteryPauseSnapshot.OriginalMode ?? "unknown"}')");
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"ApplyBatteryPause failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resumes normal charging. Idempotent — does nothing if pause was never applied.
    /// </summary>
    private void RestoreBatteryPause()
    {
        if (_batteryPauseSnapshot == null) return;
        try
        {
            _batteryPause.Resume(_batteryPauseSnapshot);
        }
        catch (Exception ex)
        {
            _log.Warn("GameBoosterService", $"RestoreBatteryPause failed: {ex.Message}");
        }
        finally
        {
            _batteryPauseSnapshot = null;
        }
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
                    // TcpDelAckTicks was missing, which is why this only half-worked:
                    // TcpAckFrequency=1 stops ACKs being batched by COUNT, but Windows still
                    // holds an ACK for the delayed-ACK TIMER (200 ms by default) until this is 0.
                    // Both have to be set or the delay you were trying to remove is still there.
                    var savedTicks = iKey.GetValue("TcpDelAckTicks");
                    iKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                    iKey.SetValue("TCPNoDelay",      1, RegistryValueKind.DWord);
                    iKey.SetValue("TcpDelAckTicks",  0, RegistryValueKind.DWord);
                    _nagleRestore.Add((path, "TcpAckFrequency", savedAck));
                    _nagleRestore.Add((path, "TCPNoDelay",      savedDelay));
                    _nagleRestore.Add((path, "TcpDelAckTicks",  savedTicks));
                }
                catch { }
            }

            _log.Info("GameBoosterService",
                $"Nagle disabled on {_nagleRestore.Count / 3} TCP adapter(s)");
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

    // ── P/Invoke: WLAN API (wlanapi.dll) — software radio toggle ─────────────
    //
    // WlanSetInterface with wlan_intf_opcode_radio_state is the same call that
    // Windows Quick Settings makes internally (via RadioManager.dll → IRadioManager).
    // It sets the SOFTWARE radio state per-interface, and Quick Settings will
    // immediately reflect the change (toggle shows grey/off).

    private enum WlanIntfOpcode : uint { RadioState = 4 }
    private enum Dot11RadioState : uint { Unknown = 0, On = 1, Off = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid   InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public int    isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanPhyRadioState
    {
        public uint          dwPhyIndex;
        public Dot11RadioState dot11SoftwareRadioState;
        public Dot11RadioState dot11HardwareRadioState;
    }

    [DllImport("wlanapi.dll", SetLastError = true)]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanSetInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        WlanIntfOpcode OpCode, uint dwDataSize, IntPtr pData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    // ·· Disable Wi-Fi when Ethernet is active ··································
    //
    // Strategy: use WlanSetInterface(wlan_intf_opcode_radio_state, Off) via wlanapi.dll.
    // This sets the SOFTWARE radio state on each Wi-Fi interface — identical to what
    // the Windows Quick Settings Wi-Fi toggle does internally. Quick Settings will
    // immediately show the toggle as grey/off.

    private void ApplyDisableWifi()
    {
        _wifiRadioDisabled = false;
        try
        {
            // Only disable Wi-Fi when at least one wired adapter is up.
            bool ethernetUp = NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.OperationalStatus == OperationalStatus.Up
                       && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                       && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                       && n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
                       && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                       && !n.Description.Contains("VPN",     StringComparison.OrdinalIgnoreCase)
                       && !n.Description.Contains("TAP",     StringComparison.OrdinalIgnoreCase));

            if (!ethernetUp)
            {
                _log.Info("GameBoosterService", "DisableWifi: no active ethernet — skipping");
                return;
            }

            _wifiRadioDisabled = SetWifiSoftwareRadio(Dot11RadioState.Off);
            _log.Info("GameBoosterService",
                _wifiRadioDisabled
                    ? "DisableWifi: Wi-Fi software radio turned off (Quick Settings will show grey)"
                    : "DisableWifi: no Wi-Fi interfaces found or WLAN API unavailable");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyDisableWifi failed: {ex.Message}"); }
    }

    private void RestoreWifi()
    {
        if (!_wifiRadioDisabled) return;
        _wifiRadioDisabled = false;
        try
        {
            bool ok = SetWifiSoftwareRadio(Dot11RadioState.On);
            _log.Info("GameBoosterService",
                ok ? "RestoreWifi: Wi-Fi software radio restored" : "RestoreWifi: WLAN API call failed");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreWifi failed: {ex.Message}"); }
    }

    /// <summary>
    /// Sets the software radio state on all Wi-Fi interfaces via wlanapi.dll.
    /// Returns true if at least one interface was updated.
    /// </summary>
    private bool SetWifiSoftwareRadio(Dot11RadioState state)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr hClient) != 0) return false;
        try
        {
            if (WlanEnumInterfaces(hClient, IntPtr.Zero, out IntPtr pList) != 0) return false;
            try
            {
                // The list struct begins with dwNumberOfItems (DWORD) + dwIndex (DWORD),
                // followed immediately by an inline array of WLAN_INTERFACE_INFO.
                uint count  = (uint)Marshal.ReadInt32(pList, 0);
                int infoSz  = Marshal.SizeOf<WlanInterfaceInfo>();
                IntPtr pArr = pList + 8; // skip dwNumberOfItems + dwIndex
                bool any    = false;

                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(pArr + i * infoSz);
                    var guid = info.InterfaceGuid;

                    // Iterate PHY indices 0..7 to cover multi-band adapters (2.4 GHz + 5 GHz + 6 GHz)
                    for (uint phy = 0; phy < 8; phy++)
                    {
                        var rs = new WlanPhyRadioState
                        {
                            dwPhyIndex             = phy,
                            dot11SoftwareRadioState = state,
                            dot11HardwareRadioState = Dot11RadioState.On,
                        };
                        int sz   = Marshal.SizeOf<WlanPhyRadioState>();
                        IntPtr p = Marshal.AllocHGlobal(sz);
                        try
                        {
                            Marshal.StructureToPtr(rs, p, false);
                            uint ret = WlanSetInterface(hClient, ref guid, WlanIntfOpcode.RadioState, (uint)sz, p, IntPtr.Zero);
                            if (ret == 0) any = true;
                            else break; // non-zero for this PHY index → no more PHYs
                        }
                        finally { Marshal.FreeHGlobal(p); }
                    }
                }
                return any;
            }
            finally { WlanFreeMemory(pList); }
        }
        finally { WlanCloseHandle(hClient, IntPtr.Zero); }
    }

    // ── Bluetooth Radio ────────────────────────────────────────────────────────

    private void ApplyDisableBluetooth()
    {
        _bluetoothRadioDisabled = false;
        try
        {
            // Only disable if the Bluetooth radio is currently on.
            // If it was already off, _bluetoothRadioDisabled stays false and RestoreBluetooth
            // will be a no-op — we never turn on something the user had deliberately turned off.
            if (!IsBluetoothRadioPresent())
            {
                _log.Info("GameBoosterService", "DisableBluetooth: no active Bluetooth radio — skipping");
                return;
            }

            _bluetoothRadioDisabled = SetBluetoothRadioEnabled(false);
            _log.Info("GameBoosterService",
                _bluetoothRadioDisabled
                    ? "DisableBluetooth: Bluetooth radio disabled"
                    : "DisableBluetooth: SetupAPI call failed — radio unchanged");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"ApplyDisableBluetooth failed: {ex.Message}"); }
    }

    private void RestoreBluetooth()
    {
        if (!_bluetoothRadioDisabled) return; // was already off before boost — don't touch it
        _bluetoothRadioDisabled = false;
        try
        {
            bool ok = SetBluetoothRadioEnabled(true);
            _log.Info("GameBoosterService",
                ok ? "RestoreBluetooth: Bluetooth radio re-enabled"
                   : "RestoreBluetooth: SetupAPI call failed — radio may need manual toggle");
        }
        catch (Exception ex) { _log.Warn("GameBoosterService", $"RestoreBluetooth failed: {ex.Message}"); }
    }

    /// <summary>
    /// Returns true if at least one Bluetooth radio device is present and currently enabled.
    /// Uses SetupDiGetClassDevs with DIGCF_PRESENT, which only returns active (non-disabled) devices.
    /// </summary>
    private static bool IsBluetoothRadioPresent()
    {
        var guid = BtRadioClassGuid;
        IntPtr devs = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (devs == SetupDiInvalidHandle) return false;
        try
        {
            var info = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            return SetupDiEnumDeviceInfo(devs, 0, ref info); // true = at least one device found
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
    }

    /// <summary>
    /// Enables or disables all Bluetooth radio devices via the SetupAPI device property-change
    /// installer (same mechanism as Device Manager Enable/Disable).
    /// Returns true if at least one device was successfully toggled.
    /// </summary>
    private static bool SetBluetoothRadioEnabled(bool enable)
    {
        var guid = BtRadioClassGuid;
        // When re-enabling: don't use DIGCF_PRESENT — disabled devices are not "present".
        // When disabling: DIGCF_PRESENT filters to only the active radio (safer).
        uint flags = enable ? 0u : DIGCF_PRESENT;
        IntPtr devs = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, flags);
        if (devs == SetupDiInvalidHandle) return false;

        bool any = false;
        try
        {
            var info = new SpDevinfoData { cbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(devs, i, ref info); i++)
            {
                var p = new SpPropchangeParams
                {
                    // SP_CLASSINSTALL_HEADER fields (cbSize = 2 DWORDs = 8 bytes)
                    HeaderCbSize      = 8,
                    InstallFunction   = DIF_PROPERTYCHANGE,
                    StateChange       = enable ? DICS_ENABLE : DICS_DISABLE,
                    Scope             = DICS_FLAG_GLOBAL,
                    HwProfile         = 0,
                };
                if (SetupDiSetClassInstallParamsW(devs, ref info, ref p, (uint)Marshal.SizeOf<SpPropchangeParams>()))
                    if (SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, devs, ref info))
                        any = true;

                // Reset cbSize for the next iteration (SetupDiCallClassInstaller may clear it)
                info.cbSize = (uint)Marshal.SizeOf<SpDevinfoData>();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devs); }
        return any;
    }

    // ── Bluetooth SetupAPI P/Invoke ────────────────────────────────────────────

    // Bluetooth Radios device class GUID — matches all Bluetooth radio adapters in Device Manager
    private static readonly Guid BtRadioClassGuid = new("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}");
    private static readonly IntPtr SetupDiInvalidHandle = new(-1);

    private const uint DIGCF_PRESENT       = 0x00000002;
    private const uint DICS_ENABLE         = 0x00000001;
    private const uint DICS_DISABLE        = 0x00000002;
    private const uint DICS_FLAG_GLOBAL    = 0x00000001;
    private const uint DIF_PROPERTYCHANGE  = 0x00000012;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint   cbSize;
        public Guid   ClassGuid;
        public uint   DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpPropchangeParams
    {
        // Inline SP_CLASSINSTALL_HEADER (2 DWORDs)
        public uint HeaderCbSize;
        public uint InstallFunction;
        // SP_PROPCHANGE_PARAMS fields
        public uint StateChange;   // DICS_ENABLE / DICS_DISABLE
        public uint Scope;         // DICS_FLAG_GLOBAL
        public uint HwProfile;     // 0 = current profile
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet, uint MemberIndex, ref SpDevinfoData DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(
        IntPtr DeviceInfoSet, ref SpDevinfoData DeviceInfoData,
        ref SpPropchangeParams ClassInstallParams, uint ClassInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(
        uint InstallFunction, IntPtr DeviceInfoSet, ref SpDevinfoData DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // ── Xbox Services Logic ────────────────────────────────────────────────────

    // ── Dispose ────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _gameCheckTimer?.Stop();
        _fastGameTimer?.Stop();
        _manualBoostTimeoutTimer?.Stop();

        if (_boostActive)
            DeactivateBoost()?.Invoke();
    }
}
