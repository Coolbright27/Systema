// ════════════════════════════════════════════════════════════════════════════
// RecommendationRules.cs  ·  What Systema recommends, decided for THIS PC
// ════════════════════════════════════════════════════════════════════════════
//
// One place that says whether a setting is recommended on the PC it's running on, and why.
// Two things read it, so they can never disagree:
//   • Home's Auto Pilot checklist and Suggestions (DashboardViewModel), for the settings that
//     only apply to some PCs (desktop vs laptop, NVIDIA or not, ...).
//   • The green "Recommended" pill on each page's setting card, with the reason on hover
//     (<ctl:SettingsCard RecommendKey="DisableMpo">, which binds to Recommend.Instance).
//
// Before this, pages printed "Recommended" as fixed text. On an NVIDIA PC the Graphics page said
// "Recommended" on Disable MPO while Auto Pilot kept MPO on (NVIDIA needs it for VSync); the Intel
// page said "Recommended: On" for power saving that Home told desktops to turn off.
//
// Every rule below mirrors what Auto Pilot or the Home suggestions already do, or what the card's
// own description already says. Nothing here changes a setting; it only labels.
// ════════════════════════════════════════════════════════════════════════════

using System.ComponentModel;
using Systema.Services;

namespace Systema.Core;

/// <summary>The facts about this PC that decide what's recommended. RamMb is installed RAM.</summary>
public sealed record PcProfile(bool IsLaptop, long RamMb, bool HasNvidiaGpu, bool HasIntelIgpu, bool IsHybridCpu)
{
    /// <summary>
    /// Reads the profile. Cheap (a power-status call, a memory call and two registry scans), but
    /// still call it off the UI thread.
    /// </summary>
    public static PcProfile Detect()
    {
        bool laptop = false, nvidia = false, intel = false, hybrid = false;
        long ram = 0;
        try { laptop = new PowerPlanService().HasBattery(); } catch { }
        try
        {
            // Installed RAM (the "16 GB" on the box), the same figure the engine uses for Keep
            // Kernel in RAM. The usable total is lower whenever graphics reserve some.
            double gb = SystemStabilityService.InstalledRamGb();
            ram = gb > 0 ? (long)Math.Round(gb * 1024) : new MemoryService().GetRamStats().totalMb;
        }
        catch { }
        try { nvidia = new NvidiaGpuService().DetectNvidiaAdapters().Count > 0; } catch { }
        try { intel = new IntelGpuService().DetectIntelAdapters().Count > 0; } catch { }
        try { hybrid = CoreParkingService.IsHybridCpu(); } catch { }
        return new PcProfile(laptop, ram, nvidia, intel, hybrid);
    }
}

/// <summary>A recommendation for one setting: the pill text (null = no pill) and why.</summary>
public readonly record struct Advice(string? Badge, string? Why)
{
    public static readonly Advice None = new(null, null);
    public bool IsRecommended => Badge != null;
}

public static class RecommendationRules
{
    // ── Device rules shared with Home's Auto Pilot checklist and Suggestions ──

    /// <summary>High Performance power plan (Auto Pilot "Power plan", Visual's Performance mode).
    /// Desktops only: on a laptop the extra heat makes it throttle.</summary>
    public static bool WantsHighPerformancePlan(PcProfile pc) => !pc.IsLaptop;

    /// <summary>Forced 0.5 ms timer (Auto Pilot "Stable timer resolution"). Desktops only: costs battery.</summary>
    public static bool WantsTimerResolution(PcProfile pc) => !pc.IsLaptop;

    /// <summary>Disable Multi-Plane Overlay (Auto Pilot). Never with NVIDIA graphics, which need
    /// MPO for VSync and Independent Flip.</summary>
    public static bool WantsMpoDisabled(PcProfile pc) => !pc.HasNvidiaGpu;

    /// <summary>Cap FPS to the monitor's refresh rate (Home suggestion). NVIDIA laptops only.</summary>
    public static bool WantsFpsCap(PcProfile pc) => pc.IsLaptop && pc.HasNvidiaGpu;

    /// <summary>NVIDIA GPU power management off, i.e. full clocks (Home suggestion). Desktops only.</summary>
    public static bool WantsNvidiaFullClocks(PcProfile pc) => !pc.IsLaptop && pc.HasNvidiaGpu;

    /// <summary>NVIDIA "Prefer maximum performance" power mode (Home suggestion). Desktops only.</summary>
    public static bool WantsNvidiaMaxPerformanceMode(PcProfile pc) => !pc.IsLaptop && pc.HasNvidiaGpu;

    /// <summary>Intel iGPU Max Performance policy and power saving off (Home suggestions). Desktops only.</summary>
    public static bool WantsIntelMaxPerformance(PcProfile pc) => !pc.IsLaptop && pc.HasIntelIgpu;

    /// <summary>Keep Kernel in RAM: the card says 16 GB or more installed.</summary>
    public static bool WantsKernelInRam(PcProfile pc) => pc.RamMb >= 16 * 1024;

    // ── Per-card pills ────────────────────────────────────────────────────

    private const string AutoPilot = "Part of Auto Pilot on every PC.";
    private const string EngineTweak = "Recommended on every PC while the Systema Engine is on.";
    private const string SimplerGraphics = "Home suggests turning this off. A simpler graphics path is more stable on many PCs. If your games felt better with it on, turning it back on is fine.";

    private static Advice Rec(string why) => new("Recommended", why);
    private static Advice RecValue(string value, string why) => new($"Recommended: {value}", why);

    private static readonly Dictionary<string, Func<PcProfile, Advice>> Rules = new(StringComparer.Ordinal)
    {
        // Audio
        ["DisableDucking"]  = _ => Rec("Keeps your music and games at full volume during calls. Harmless on any PC."),
        ["AudioScheduling"] = _ => Rec(AutoPilot + " Steadier sound when the PC is busy."),

        // Graphics
        ["DisableMpo"] = pc => WantsMpoDisabled(pc)
            ? Rec(AutoPilot.Replace("every PC", "PCs without NVIDIA graphics") + " Steadier frame timing.")
            : RecValue("Off", "This PC has NVIDIA graphics, which need Multi-Plane Overlay for VSync and Independent Flip."),
        ["Hags"]                  = _ => RecValue("Off", SimplerGraphics),
        ["WindowedOptimizations"] = _ => RecValue("Off", SimplerGraphics),
        ["GameDvrOff"]            = _ => Rec("Stops Game Bar recording in the background, which costs frames."),
        ["PriorityGraphics"]      = _ => Rec(AutoPilot),
        ["TimerResolution"]       = pc => WantsTimerResolution(pc)
            ? Rec("Auto Pilot turns this on for desktops: steadier frame pacing.")
            : Advice.None,
        ["GpuRecoveryTimeout"]    = _ => Rec(AutoPilot + " Fewer black-screen GPU resets."),

        // Systema Engine
        ["LaunchBoost"]          = _ => Rec(AutoPilot + " Apps open faster."),
        ["ForegroundBoost"]      = _ => Rec(AutoPilot),
        ["MaxResponsiveness"]    = _ => Rec(AutoPilot),
        ["InstantAppFocus"]      = _ => Rec(EngineTweak),
        ["InstantStartupApps"]   = _ => Rec(EngineTweak),
        ["NetworkThrottlingOff"] = _ => Rec(EngineTweak),
        ["FastAppClose"]         = _ => Rec(EngineTweak),
        ["FasterShutdown"]       = _ => Rec(EngineTweak),
        ["InputHookTimeout"]     = _ => Rec(EngineTweak),
        ["ServiceShutdownFast"]  = _ => Rec(EngineTweak),
        ["FastStartupOff"]       = _ => Rec(EngineTweak),
        ["BackgroundAppsOff"]    = _ => Rec(EngineTweak),
        ["KeepKernelInRam"]      = pc => WantsKernelInRam(pc)
            ? Rec($"This PC has {Math.Round(pc.RamMb / 1024.0)} GB of RAM, enough to keep the kernel in memory.")
            : Advice.None,
        ["PowerThrottling"]      = pc => pc.IsLaptop
            ? Rec("On a laptop, slowing background apps saves battery and heat.")
            : pc.IsHybridCpu
                ? Rec("This CPU has efficiency cores, so background apps run on those.")
                : Advice.None,

        // System Tweaks
        ["PreviewUpdates"] = _ => Rec(AutoPilot),
        ["Dns"]            = _ => RecValue("Cloudflare", AutoPilot + " Fast and private."),
        ["CoreParking"]    = _ => Rec(AutoPilot + " Less heat and power when the PC isn't busy."),
        ["NtfsLastAccess"] = _ => Rec(AutoPilot + " Fewer needless disk writes."),
        ["Suggestions"]    = _ => Rec("Home suggests this on every PC."),
        ["WebSearch"]      = _ => Rec("Home suggests this on every PC."),

        // Cleanup & Privacy
        ["PrivacyCleanup"] = _ => Rec(AutoPilot),
        ["NoTelemetryPro"] = _ => Rec(AutoPilot),

        // Visual & Power
        ["PerformanceMode"] = pc => WantsHighPerformancePlan(pc)
            ? Rec("Auto Pilot keeps desktops on High Performance.")
            : Advice.None,
        ["DragContents"] = _ => Rec("Windows' own default. Turning it off saves almost nothing."),
        ["ClearType"]    = _ => Rec("Keeps text sharp. Windows' own default."),

        // Game Booster
        ["GameBoost"]          = _ => Rec(AutoPilot),
        ["BoostHighPerfPlan"]  = pc => WantsHighPerformancePlan(pc)
            ? Rec("Desktops have the cooling for full speed while gaming.")
            : Advice.None,
        // No pill on "Pause battery charging": most laptops can't do it (the switch is disabled
        // unless the vendor tool exposes charge control), and a pill on a dead switch only confuses.

        // Settings
        ["LaunchOnStartup"] = _ => Rec(AutoPilot + " Systema can only keep things tuned while it's running."),

        // Intel graphics
        ["IntelPowerPolicy"] = pc => WantsIntelMaxPerformance(pc)
            ? RecValue("Max Performance", "Desktops have the cooling and power to keep the graphics at full clocks. Home suggests this too.")
            : RecValue("Default", "On a laptop, full graphics clocks mostly add heat, and a hot chip slows itself down. The driver's default balances it."),
        ["IntelRc6"]  = IntelPowerSaving,
        ["IntelDpst"] = IntelPowerSaving,
        ["IntelDrrs"] = IntelPowerSaving,
        ["IntelFbc"]  = _ => RecValue("On", "Saves power with no visible downside."),

        // NVIDIA graphics
        ["NvidiaPowerManagement"] = pc => WantsNvidiaFullClocks(pc)
            ? RecValue("Off", "Desktops can hold full clocks. Home suggests this too.")
            : RecValue("On", "On a laptop, full clocks make heat, and a hot GPU throttles itself."),
        ["NvidiaModePluggedIn"] = pc => WantsNvidiaMaxPerformanceMode(pc)
            ? RecValue("Prefer maximum performance", "Desktops have the power and cooling to hold full clocks. Home suggests this too.")
            : Advice.None,
        ["NvidiaModeBattery"] = pc => pc.IsLaptop
            ? RecValue("Optimal power", "Full clocks on battery mostly make heat and drain the battery.")
            : Advice.None,
        ["NvidiaFpsCap"] = pc => WantsFpsCap(pc)
            ? Rec("Frames above your screen's refresh rate are thrown away. Capping saves heat and battery.")
            : Advice.None,
    };

    private static Advice IntelPowerSaving(PcProfile pc) => WantsIntelMaxPerformance(pc)
        ? RecValue("Off", "A desktop has no battery to save, so the graphics can stay fully awake.")
        : RecValue("On", "Saves battery and keeps the laptop cooler.");

    /// <summary>Every key a page may ask about (tests check pages only use these).</summary>
    public static IReadOnlyCollection<string> Keys => Rules.Keys;

    /// <summary>The advice for <paramref name="key"/> on <paramref name="pc"/>; no pill for unknown keys.</summary>
    public static Advice For(string key, PcProfile pc) =>
        Rules.TryGetValue(key, out var rule) ? rule(pc) : Advice.None;
}

/// <summary>
/// XAML's handle on the rules. SettingsCard.RecommendKey binds <c>[DisableDucking]</c> (the pill
/// text, or nothing) and <c>[DisableDucking.Why]</c> (the reason, shown on hover).
/// Empty until the PC profile has been read at startup, then every pill updates at once.
/// </summary>
public sealed class Recommend : INotifyPropertyChanged
{
    public static Recommend Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public PcProfile? Profile { get; private set; }

    public string? this[string key]
    {
        get
        {
            if (Profile == null) return null;
            bool why = key.EndsWith(".Why", StringComparison.Ordinal);
            var advice = RecommendationRules.For(why ? key[..^4] : key, Profile);
            return why ? advice.Why : advice.Badge;
        }
    }

    /// <summary>Sets the profile and refreshes every bound pill. Call on the UI thread.</summary>
    public void SetProfile(PcProfile profile)
    {
        Profile = profile;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
