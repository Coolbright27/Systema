// ════════════════════════════════════════════════════════════════════════════
// LaunchBoostRules.cs  ·  Who gets a Launch Boost, as ordered rules
// ════════════════════════════════════════════════════════════════════════════
//
// Launch Boost (Services/TaskSleepService.LaunchBoost.cs) sees every new process twice over (the
// process-start event and a 300 ms poll) and asks these rules whether to boost it. They used to be
// if/return chains across four methods (ShouldLaunchBoost, IsUserLaunch, IsBoostableLaunch,
// IsBoostableChild); here they're two lists you can read top to bottom, and they're pure, so every
// rule is unit-tested (LaunchBoostRulesTests).
//
// Nothing about who gets boosted changed in the move (0.7.365): same rules, same order, and the
// slow facts (a process lookup, a full process snapshot) are still only fetched when a rule asks.
//
//   LaunchBoostNames  — names never boosted, whoever started them
//   LaunchBoostRules  — boost it, let it ride its parent's boost, or leave it alone
//
// RELATED FILES
//   Services/TaskSleepService.LaunchBoost.cs — gathers the facts, applies and ends boosts
//   Services/TaskSleepService.SkipRules.cs   — "Launch Boost active" keeps a boosting app from napping

namespace Systema.Core;

/// <summary>
/// Programs that are never boosted, whoever started them. FIRST MATCH WINS (the rule's name is the
/// reason). A boost only helps something the user is opening; these are Windows, security software,
/// Systema itself, or short-lived helpers that spawn in bursts.
/// </summary>
internal static class LaunchBoostNames
{
    /// <summary>
    /// Command-line tools and background helpers that are never something the user "opens".
    /// Boosting them wastes the slot, grows the boost list the 300 ms tick re-asserts, and a burst of
    /// them (a terminal running commands, NGEN after an update) floods the log.
    /// </summary>
    public static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Command-line / scripting / config utilities
        "cmd", "powershell", "pwsh", "wscript", "cscript", "reg", "regedit",
        "schtasks", "sc", "fsutil", "powercfg", "where", "whoami", "tasklist",
        "taskkill", "wmic", "net", "net1", "netsh", "ipconfig", "nslookup",
        "ping", "tracert", "route", "arp", "icacls",
        // Dev-shell subprocesses that spawn in bursts (a terminal / IDE / dev tool running
        // commands): git & Unix coreutils from Git-Bash/WSL. Transient, sub-second, and boosting
        // dozens of them is pure noise — this is the flood in the diagnostic report.
        "git", "git-lfs", "bash", "sh", "conhost", "openconsole",
        "findstr", "find", "cat", "head", "tail", "grep", "sed", "awk", "ls",
        "sort", "wc", "cut", "tr", "xargs", "more", "dirname", "basename", "env",
        "cygpath", "uname", "date", "sleep", "printf", "expr", "test", "true", "false",
        "timeout", "choice",
        // UAC and user-mode security prompts
        "consent", "RuntimeBroker",
        // .NET Framework NGEN — fires in big bursts after every Windows Update;
        // boosting twenty of these at once is what overwhelms the boost dict and
        // hammers the GPU scheduler with priority calls.
        "mscorsvw", "ngen", "ngentask",
        // Windows Update / servicing
        "TiWorker", "TrustedInstaller", "Dism", "DismHost", "wimserv",
        "sdbinst", "wuaucltcore", "MoUsoCoreWorker", "MusNotification",
        "MusNotifyIcon", "musnotificationux",
        // Telemetry / compatibility scans (Microsoft's own background tasks)
        "CompatTelRunner", "DeviceCensus", "deviceenroller", "diskaudit",
        // Modern Windows shell hosts / picker hosts (system internal UI)
        "UIEOrchestrator", "UIEOrchestratorStub", "PickerHost",
        "DataExchangeHost", "ShellHost", "CrossDeviceResume",
        // Volume / Disk / Firmware system services
        "vds", "vdsldr", "VSSVC", "FirmwareTPM",
        // Generic Win32 helpers used by system tasks
        "rundll32", "regsvr32", "CompPkgSrv", "OpenWith",
        // Search indexing helpers
        "SearchFilterHost", "SearchProtocolHost", "WmiApSrv",
        // Crash / error reporters
        "crashreporter", "crashhelper", "WerFault", "WerFaultSecure",
        // CHX SmartScreen helper
        "CHXSmartScreen",
        // Dell / OEM inventory + driver-update agents seen on test machines
        "invcol", "DRVUpdate", "SalomanDock", "provtool",
        // Background updaters and scanners nobody opens by hand (Edge, Defender signatures,
        // NVIDIA DLSS, NVIDIA App's game scanner)
        "MicrosoftEdgeUpdate", "MpSigStub", "nvngx_update", "OAWrapper",
        // Background "open hint" prompts
        "downloader", "updatesrv", "pingsender", "ByteCodeGenerator",
        // Our own installer (the previous build's setup) — never boost it
        "Systema_Setup",
    };

    private sealed record Rule(string Name, Func<string, NameLists, bool> Matches);

    /// <summary>The Windows / security name lists, which live in the nap engine.</summary>
    public readonly record struct NameLists(Func<string, bool> IsWindowsProcess, Func<string, bool> IsSecuritySoftware);

    private static readonly Rule[] Rules =
    {
        new("No name",                              (n, _) => string.IsNullOrEmpty(n)),
        new("Systema itself",                       (n, _) => n.Equals("Systema", StringComparison.OrdinalIgnoreCase)),
        new("Windows process",                      (n, l) => l.IsWindowsProcess(n)),
        new("Security software",                    (n, l) => l.IsSecuritySoftware(n)),
        new("Command-line tool or background helper", (n, _) => Helpers.Contains(n)),
        // Defensive prefix match for versioned names like "Systema_Setup_0.7.28" and its temp helpers.
        new("Systema's installer",                  (n, _) => n.StartsWith("Systema_Setup", StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>The rule names, in order (pinned by LaunchBoostRulesTests).</summary>
    public static IEnumerable<string> RuleNames => Rules.Select(r => r.Name);

    /// <summary>Why this name is never boosted, or null if it can be.</summary>
    public static string? NeverBoostReason(string? name, NameLists lists)
    {
        string n = name ?? "";
        foreach (var rule in Rules)
            if (rule.Matches(n, lists)) return rule.Name;
        return null;
    }
}

/// <summary>What the launch rules know about one new process.</summary>
/// <remarks>
/// The parent facts are Lazy on purpose: most launches are decided by the cheap facts, so the costly
/// ones (a process lookup for the name and age, a full process snapshot for "did the shell start
/// it") are only fetched when a rule gets that far, exactly as the old if-chain did.
/// </remarks>
internal sealed record LaunchFacts(
    /// <summary>It passes <see cref="LaunchBoostNames"/> (not Windows, security, Systema, or a helper).</summary>
    bool Boostable,
    int ParentPid,
    /// <summary>Its parent has a Launch Boost running right now.</summary>
    bool ParentBoosted,
    Lazy<bool> ParentNapped,
    /// <summary>Null when the parent has already exited.</summary>
    Lazy<string?> ParentName,
    /// <summary>Null when it can't be read.</summary>
    Lazy<TimeSpan?> ParentAge,
    Lazy<bool> ParentStartedByShell);

internal enum LaunchAction
{
    /// <summary>Leave it alone.</summary>
    Skip,
    /// <summary>A new boost, for the full boost duration.</summary>
    Boost,
    /// <summary>Boost it, ending when its parent's boost ends (one window for the whole app).</summary>
    RideParentBoost,
}

internal readonly record struct LaunchDecision(LaunchAction Action, string Rule);

internal static class LaunchBoostRules
{
    /// <summary>A parent younger than this that the shell started is a launcher stub.</summary>
    public static readonly TimeSpan LauncherStubMaxAge = TimeSpan.FromSeconds(20);

    /// <summary>Service, scheduler and updater hosts: what they start is background work, not a launch.</summary>
    public static readonly HashSet<string> BackgroundParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "services", "svchost", "taskhostw", "wininit", "winlogon", "lsass", "WmiPrvSE",
        "dllhost", "RuntimeBroker", "sihost", "MoUsoCoreWorker", "usocoreworker", "UsoClient",
        "wuauclt", "TrustedInstaller", "TiWorker", "OfficeClickToRun", "backgroundTaskHost",
        "smartscreen", "SearchIndexer", "SearchProtocolHost", "SgrmBroker",
    };

    /// <summary>
    /// Shells and interpreters RUN commands rather than open apps. A child of one is a script or tool
    /// subprocess, so it never starts a boost of its own (otherwise a terminal or dev tool cascades
    /// boosts across its whole subprocess tree). Game launchers like Steam/Epic are deliberately not here.
    /// </summary>
    public static readonly HashSet<string> ShellParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "wsl", "wslhost", "conhost", "openconsole",
        "node", "python", "python3", "py", "perl", "ruby", "git",
    };

    private sealed record Rule(string Name, Func<LaunchFacts, bool> Applies, LaunchAction Action);

    private static bool Young(LaunchFacts f) => f.ParentAge.Value is { } age && age < LauncherStubMaxAge;

    /// <summary>
    /// The rules, FIRST MATCH WINS (order is load-bearing, exactly as the old if-chain ran):
    ///   1-2. Its parent is boosting: an app rides that same boost window, anything else is skipped.
    ///   3.   Not an app (LaunchBoostNames)                  → skip
    ///   4-6. No parent, a sleeping parent, or a parent that's gone → skip (can't be a user launch)
    ///   7.   Started by a service / scheduler / updater host → skip
    ///   8.   Started by the shell (Start, taskbar, desktop, Run, a double-clicked file) → BOOST
    ///   9.   Started by a command shell or script           → skip
    ///   10.  Started by a launcher stub the shell opened (Firefox and friends launch that way) → BOOST
    ///   11.  Started by a young process something else started (an updater relaunching itself) → skip
    ///   12.  Started by an app that's already running       → skip (not a fresh launch)
    /// </summary>
    private static readonly Rule[] Rules =
    {
        new("Rides its parent's boost",             f => f.ParentBoosted && f.Boostable,   LaunchAction.RideParentBoost),
        new("Parent is boosting, but this isn't an app", f => f.ParentBoosted,             LaunchAction.Skip),
        new("Not an app",                           f => !f.Boostable,                      LaunchAction.Skip),
        new("No parent",                            f => f.ParentPid <= 0,                  LaunchAction.Skip),
        new("Started by a sleeping app",            f => f.ParentNapped.Value,              LaunchAction.Skip),
        new("Parent already gone",                  f => f.ParentName.Value == null,        LaunchAction.Skip),
        new("Started by a background host",         f => BackgroundParents.Contains(f.ParentName.Value!), LaunchAction.Skip),
        new("Opened from the shell",                f => f.ParentName.Value!.Equals("explorer", StringComparison.OrdinalIgnoreCase), LaunchAction.Boost),
        new("Started by a command shell or script", f => ShellParents.Contains(f.ParentName.Value!), LaunchAction.Skip),
        new("Opened through a launcher stub",       f => Young(f) && f.ParentStartedByShell.Value, LaunchAction.Boost),
        new("Started by a young background process", Young,                                 LaunchAction.Skip),
        new("Started by an app that's already running", _ => true,                          LaunchAction.Skip),
    };

    /// <summary>The rule names, in order (pinned by LaunchBoostRulesTests).</summary>
    public static IEnumerable<string> RuleNames => Rules.Select(r => r.Name);

    public static LaunchDecision Decide(LaunchFacts f)
    {
        foreach (var rule in Rules)
            if (rule.Applies(f)) return new(rule.Action, rule.Name);
        return new(LaunchAction.Skip, "");   // unreachable: the last rule always applies
    }
}
