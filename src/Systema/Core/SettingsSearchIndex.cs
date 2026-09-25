// ════════════════════════════════════════════════════════════════════════════
// SettingsSearchIndex.cs  ·  What Ctrl+K search can find
// ════════════════════════════════════════════════════════════════════════════
//
// One entry per setting: the page it lives on (the same key NavigateCommand takes), its title
// EXACTLY as the page shows it, and the everyday words people type instead of our names for
// things ("fan" finds the thermal settings, "tracking" finds No Telemetry Pro).
//
// The title doubles as the jump target: after navigating, MainWindow finds the TextBlock whose
// text equals it and highlights that row. UiSearchIndexTests checks that every title still
// appears on its page, so renaming a setting without updating this list fails the build
// instead of producing a search result that lands nowhere.
// ════════════════════════════════════════════════════════════════════════════

namespace Systema.Core;

public sealed record SearchEntry(string Section, string Title, string Keywords, bool IsPage = false);

public static class SettingsSearchIndex
{
    /// <summary>Nav key → the name shown in the sidebar.</summary>
    public static readonly IReadOnlyDictionary<string, string> SectionNames = new Dictionary<string, string>
    {
        ["Dashboard"]   = "Home",
        ["Memory"]      = "Memory & Startup",
        ["Services"]    = "Cleanup & Privacy",
        ["Visual"]      = "Visual & Power",
        ["GameBooster"] = "Game Booster",
        ["Tools"]       = "System Tweaks",
        ["TaskSleep"]   = "Systema Engine",
        ["Bloatware"]   = "App Cleanup",
        ["Graphics"]    = "Graphics",
        ["Audio"]       = "Audio",
        ["Intel"]       = "Intel Graphics",
        ["Nvidia"]      = "Nvidia Graphics",
        ["Dell"]        = "Dell",
        ["Settings"]    = "Settings",
    };

    public static readonly IReadOnlyList<SearchEntry> Entries = new List<SearchEntry>
    {
        // Home
        new("Dashboard", "Auto Pilot", "autopilot automatic optimize everything one click"),
        new("Dashboard", "Suggestions for this PC", "recommendations suggested tips"),

        // Memory & Startup
        new("Memory", "Free up memory", "ram clean cleaner purge standby"),
        new("Memory", "Speed up your startup", "boot login startup apps faster"),
        new("Memory", "All startup apps", "startup programs autostart run at login"),
        new("Memory", "Advanced · Virtual memory (page file)", "pagefile page file swap virtual memory"),

        // Cleanup & Privacy
        new("Services", "No Telemetry Pro", "telemetry tracking privacy data collection diagnostics edge nvidia intel spying"),
        new("Services", "Service Cleanup", "services background disable windows services"),
        new("Services", "Windows extras", "optional features extras"),

        // Visual & Power
        new("Visual", "Performance mode", "power plan high performance speed"),
        new("Visual", "Battery mode", "battery saver laptop runtime"),
        new("Visual", "Customize individual effects", "animations effects visual appearance"),
        new("Visual", "Show shadows under windows", "shadow animation effect"),
        new("Visual", "Taskbar hover previews", "taskbar thumbnail preview"),
        new("Visual", "Desktop peek preview", "aero peek desktop"),
        new("Visual", "Smooth-scroll list boxes", "smooth scrolling animation"),

        // Game Booster
        new("GameBooster", "Boost games automatically", "game boost fps gaming performance"),
        new("GameBooster", "Boost right now", "manual boost start now"),
        new("GameBooster", "Keep the PC awake", "sleep awake screen off stay on"),
        new("GameBooster", "Pause battery charging", "battery charge charging pause"),
        new("GameBooster", "Pause search indexing", "indexer indexing search"),
        new("GameBooster", "Silence notifications", "do not disturb focus notifications toasts"),
        new("GameBooster", "Disable Nagle's algorithm", "nagle latency ping lag network"),
        new("GameBooster", "Turn off Wi-Fi when you're on a cable", "wifi ethernet cable wireless"),
        new("GameBooster", "Turn off Bluetooth", "bluetooth radio"),
        new("GameBooster", "Use the High performance power plan", "power plan high performance"),
        new("GameBooster", "Free up memory before the game loads", "ram memory trim game"),

        // System Tweaks
        new("Tools", "CPU Core Efficiency", "core parking cpu heat cool quiet fan temperature"),
        new("Tools", "Block Preview Updates", "windows update preview optional updates"),
        new("Tools", "DNS Switcher", "dns cloudflare google quad9 internet"),
        new("Tools", "Disable NTFS Last-Access Timestamps", "ntfs disk ssd timestamps"),
        new("Tools", "Disable Suggestions & Nags", "ads tips nags suggestions"),
        new("Tools", "Disable Web Search in Start", "bing start menu web search"),
        new("Tools", "Realtek App Cleanup", "realtek audio console"),
        new("Tools", "Sleep → Hibernate (Battery)", "hibernate sleep battery"),
        new("Tools", "Sleep → Hibernate (Plugged In)", "hibernate sleep plugged in ac"),

        // Systema Engine
        new("TaskSleep", "Sleep rules", "nap sleep background apps task sleep"),
        new("TaskSleep", "Sleep unused apps", "nap sleep unused background"),
        new("TaskSleep", "Sleep minimized apps", "nap minimized"),
        new("TaskSleep", "Sleep tray apps", "nap tray notification area"),
        new("TaskSleep", "Hard CPU limit for sleeping apps", "cpu cap limit throttle"),
        new("TaskSleep", "Never-nap list", "exclude exceptions whitelist never sleep"),
        new("TaskSleep", "Launch Boost", "app launch faster open startup speed"),
        new("TaskSleep", "Responsiveness boosts", "responsive snappy tweaks"),
        new("TaskSleep", "Foreground Priority Boost", "foreground priority"),
        new("TaskSleep", "Instant App Focus", "focus switch alt tab"),
        new("TaskSleep", "Instant Startup Apps", "startup delay login"),
        new("TaskSleep", "Keep Kernel in RAM", "paging executive kernel"),
        new("TaskSleep", "Fast App Close", "close apps quickly shutdown"),
        new("TaskSleep", "Faster Shutdown", "shutdown restart faster"),
        new("TaskSleep", "Maximum System Responsiveness", "mmcss system responsiveness"),
        new("TaskSleep", "Network Throttling Off", "network throttling"),
        new("TaskSleep", "Disable Fast Startup", "fast startup hiberboot"),
        new("TaskSleep", "Disable background Store apps", "store apps uwp background"),
        new("TaskSleep", "Live monitor", "processes monitor napped live"),
        new("TaskSleep", "Compress napped app memory", "memory compress napped"),

        // App Cleanup
        new("Bloatware", "Safe Pre-installed App List", "bloatware uninstall remove preinstalled apps"),

        // Graphics
        new("Graphics", "Hardware-accelerated GPU Scheduling", "hags gpu scheduling"),
        new("Graphics", "Optimizations for windowed games", "windowed games flip"),
        new("Graphics", "Disable Multi-Plane Overlay (MPO)", "mpo flicker tearing overlay"),
        new("Graphics", "Turn off Game Bar capture", "game bar xbox dvr recording capture"),
        new("Graphics", "Priority graphics scheduling", "mmcss graphics priority"),
        new("Graphics", "Stable timer resolution (0.5 ms)", "timer resolution latency"),
        new("Graphics", "Extend GPU recovery timeout", "tdr gpu crash driver timeout"),

        // Audio
        new("Audio", "Disable all audio & mic enhancements", "audio enhancements microphone mic"),
        new("Audio", "Disable spatial audio", "spatial sound sonic"),
        new("Audio", "Priority audio scheduling", "audio crackle stutter mmcss"),
        new("Audio", "Stop lowering other sounds during calls", "ducking calls volume"),

        // Intel Graphics
        new("Intel", "Power Policy", "intel gpu performance power"),
        new("Intel", "Display Power Saving (DPST)", "dpst brightness backlight"),
        new("Intel", "Dynamic Refresh Switching", "drrs refresh rate hz"),
        new("Intel", "Frame Buffer Compression", "fbc compression"),
        new("Intel", "RC6 Render Standby", "rc6 standby idle"),

        // Nvidia Graphics
        new("Nvidia", "Max Frame Rate (FPS cap)", "fps cap limit framerate"),
        new("Nvidia", "Power management mode", "nvidia power performance"),

        // Dell
        new("Dell", "Thermal Profile", "fan quiet cool ultra performance heat thermal bios"),
        new("Dell", "Charging Mode", "battery charge custom 80 adaptive express always ac"),

        // Settings
        new("Settings", "Automatic updates", "update updates version"),
        new("Settings", "Launch on Windows startup", "startup boot login autostart"),
        new("Settings", "Keep Systema Running", "tray background running"),
        new("Settings", "Diagnostic Report", "logs bug report support diagnostics"),
        new("Settings", "Export / Import Settings", "backup restore export import"),
        new("Settings", "Restore Point Manager", "system restore point"),
        new("Settings", "Reset all settings", "reset defaults"),
    };

    /// <summary>
    /// Ranks entries for <paramref name="query"/>. Every word typed must appear in the title,
    /// the page name or the keywords; titles that start with the query rank first. Settings on
    /// pages this PC doesn't show (no Intel GPU, not a Dell) are left out, so search never
    /// offers a result that leads nowhere. An empty query lists the pages.
    /// </summary>
    public static IReadOnlyList<SearchEntry> Search(string query, Func<string, bool> sectionVisible, int max = 12)
    {
        var pages = SectionNames.Where(p => sectionVisible(p.Key))
                                .Select(p => new SearchEntry(p.Key, p.Value, "", IsPage: true))
                                .ToList();

        string q = (query ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return pages;

        string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(int Score, int Order, SearchEntry Entry)>();
        int order = 0;

        foreach (var e in pages.Concat(Entries.Where(e => sectionVisible(e.Section))))
        {
            order++;
            string title = e.Title.ToLowerInvariant();
            string hay   = $"{title} {SectionNames[e.Section].ToLowerInvariant()} {e.Keywords}";
            if (!words.All(w => hay.Contains(w, StringComparison.Ordinal))) continue;

            int score = title.StartsWith(q, StringComparison.Ordinal) ? 3
                      : title.Contains(q, StringComparison.Ordinal)   ? 2
                      : 1;
            scored.Add((score, order, e));
        }

        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order)
                     .Take(max).Select(s => s.Entry).ToList();
    }
}
