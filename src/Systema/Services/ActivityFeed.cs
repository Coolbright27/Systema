// ════════════════════════════════════════════════════════════════════════════
// ActivityFeed.cs  ·  The "Today" list on Home
// ════════════════════════════════════════════════════════════════════════════
//
// Turns a handful of meaningful log lines into plain sentences: games boosted, apps rested,
// launches sped up, settings put back after something else changed them, updates found.
// This is Home's proof that Systema is working, which the old page never showed.
//
// It LISTENS to the logger (LoggerService.EntryLogged) instead of being called from each
// service, so no service had to change to feed it. The trade-off is that it matches log text.
// ActivityFeedTests pins every anchor below to the exact string in its source file, so a
// reworded log line fails the build instead of silently dropping out of the feed.
//
// Frequent events (naps, launch boosts) roll up into ONE line per 10-minute window that
// updates in place ("Rested Discord and 3 other apps you weren't using"), so the feed stays
// readable. The listener runs on whichever thread logged: it only does string checks and
// posts the result to the UI dispatcher.
// ════════════════════════════════════════════════════════════════════════════

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Systema.Services;

public sealed partial class ActivityEntry : ObservableObject
{
    public ActivityEntry(DateTime when, string text) { When = when; _text = text; }
    public DateTime When { get; }
    [ObservableProperty] private string _text;
    public string TimeText => When.ToString("h:mm tt");
}

internal enum FeedKind { Line, Nap, Launch }

internal readonly record struct FeedEvent(FeedKind Kind, string Subject, string Text);

public sealed class ActivityFeed
{
    public static ActivityFeed Instance { get; } = new();

    /// <summary>Newest first. UI thread only.</summary>
    public ObservableCollection<ActivityEntry> Entries { get; } = new();

    private const int MaxEntries = 30;   // Home shows the newest 10 and "Show more" for the rest
    private static readonly TimeSpan RollupWindow  = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DuplicateSpan = TimeSpan.FromMinutes(2);

    private Dispatcher? _dispatcher;
    private readonly Dictionary<FeedKind, (ActivityEntry Entry, DateTime Start, List<string> Names)> _rollups = new();

    // TaskSleepService logs "{action}: {name} (PID n) — detail" through AddEvent. These are the
    // actions that mean "an app was put to rest". Re-napping and Child Nap are left out: they are
    // the same app again, or a helper process of an app already counted.
    internal static readonly string[] NapActions =
        { "Napping", "Deep Sleep", "Idle Nap", "Background Nap", "Minimize Nap", "Hidden Nap", "Tray Nap" };

    private ActivityFeed() { }

    /// <summary>Starts listening. Call once, on the UI thread, early in startup.</summary>
    public void Start(Dispatcher dispatcher)
    {
        if (_dispatcher != null) return;
        _dispatcher = dispatcher;
        LoggerService.Instance.EntryLogged += OnEntryLogged;
    }

    private void OnEntryLogged(LogEntry e)
    {
        if (e.Level != LogLevel.Info) return;
        var ev = Interpret(e.Source, e.Message);
        if (ev is null || _dispatcher is null) return;
        var when = e.Timestamp;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Apply(ev.Value, when)));
    }

    // ── Pure mapping from a log line to a feed event. Tested directly. ─────────
    internal static FeedEvent? Interpret(string source, string message)
    {
        switch (source)
        {
            case "TaskSleepService":
                foreach (var action in NapActions)
                    if (message.StartsWith(action + ": ", StringComparison.Ordinal))
                        return Named(FeedKind.Nap, message[(action.Length + 2)..]);
                if (message.StartsWith("Launch Boost: ", StringComparison.Ordinal) &&
                    message.Contains(" (PID ", StringComparison.Ordinal))
                    return Named(FeedKind.Launch, message["Launch Boost: ".Length..]);
                return null;

            case "GameBoosterService":
                if (message.StartsWith("Boost activated for: ", StringComparison.Ordinal))
                    return Line($"Game Boost started for {PrettyGame(message["Boost activated for: ".Length..])}.");
                if (message.StartsWith("Game session ended", StringComparison.Ordinal))
                    return Line("Game Boost ended and put everything back the way it was.");
                return null;

            case "CoreParkingService":
                if (message.StartsWith("Parking value reset by something else", StringComparison.Ordinal))
                    return Line("Another app changed core parking, so Systema put it back.");
                if (message.StartsWith("Power plan changed", StringComparison.Ordinal))
                    return Line("The power plan changed, so Systema re-applied core parking.");
                return null;

            case "DashboardViewModel":
                if (message.StartsWith("Auto-Pilot Mode ON", StringComparison.Ordinal))
                    return Line("Auto Pilot turned on and is applying every recommended optimization.");
                if (message.StartsWith("Auto-Pilot Mode OFF", StringComparison.Ordinal))
                    return Line("Auto Pilot turned off. Your settings stay as they are.");
                return null;

            case "AutoUpdate":
                if (message.StartsWith("Update found: ", StringComparison.Ordinal))
                    return Line($"Found Systema {message["Update found: ".Length..].Trim()}. It installs when your PC is idle.");
                return null;

            case "BatteryPauseService":
                // "SetChargeMode('Custom:75:80') via DellModern → OK"
                if (message.StartsWith("SetChargeMode('", StringComparison.Ordinal) && message.EndsWith("→ OK", StringComparison.Ordinal))
                {
                    int end = message.IndexOf("')", StringComparison.Ordinal);
                    if (end > 15) return Line($"Charging mode set to {PrettyCharge(message[15..end])}.");
                }
                return null;

            case "ThermalManagementService":
                // "SetMode('UltraPerformance') applied — BIOS now reports '…'"
                if (message.StartsWith("SetMode('", StringComparison.Ordinal) && message.Contains("') applied", StringComparison.Ordinal))
                {
                    int end = message.IndexOf("')", StringComparison.Ordinal);
                    if (end > 9) return Line($"Thermal profile set to {PrettyThermal(message[9..end])}.");
                }
                return null;
        }
        return null;
    }

    private static FeedEvent Line(string text) => new(FeedKind.Line, "", text);

    /// <summary>"firefox (PID 12) — app minimized" or "firefox (+2 more)" → the app name.</summary>
    private static FeedEvent? Named(FeedKind kind, string rest)
    {
        int cut = rest.IndexOf(" (", StringComparison.Ordinal);
        string name = (cut > 0 ? rest[..cut] : rest).Trim();
        if (name.Length == 0) return null;
        return new FeedEvent(kind, PrettyApp(name), "");
    }

    internal static string PrettyApp(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// <summary>"Minecraft.Windows" → "Minecraft". Store-app process names carry a suffix.</summary>
    internal static string PrettyGame(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.IndexOf('.');
        return PrettyApp(dot > 0 ? name[..dot] : name);
    }

    internal static string PrettyCharge(string mode)
    {
        if (mode.StartsWith("Custom:", StringComparison.OrdinalIgnoreCase))
        {
            var p = mode.Split(':');
            return p.Length >= 3 ? $"Custom ({p[1]} to {p[2]}%)" : "Custom";
        }
        return mode switch
        {
            "PrimAcUse" => "Always AC",
            "Express"   => "ExpressCharge",
            _           => mode,
        };
    }

    internal static string PrettyThermal(string mode) => mode switch
    {
        "UltraPerformance" => "Ultra Performance",
        _                  => mode,
    };

    internal static string RollupText(FeedKind kind, IReadOnlyList<string> names)
    {
        string a = names[0];
        int n = names.Count;
        return kind switch
        {
            FeedKind.Nap => n switch
            {
                1 => $"Rested {a} while you weren't using it.",
                2 => $"Rested {a} and {names[1]} while you weren't using them.",
                _ => $"Rested {a} and {n - 1} other apps you weren't using.",
            },
            FeedKind.Launch => n switch
            {
                1 => $"Gave {a} a faster start.",
                2 => $"Gave {a} and {names[1]} a faster start.",
                _ => $"Gave {a} and {n - 1} other apps a faster start.",
            },
            _ => a,
        };
    }

    // ── UI thread ─────────────────────────────────────────────────────────────
    private void Apply(FeedEvent ev, DateTime when)
    {
        try
        {
            if (ev.Kind == FeedKind.Line)
            {
                // The same message twice in a row (a plan switch fires two events, a thermal mode
                // re-applies on plug and unplug) reads as a glitch, so collapse it.
                if (Entries.Count > 0 && Entries[0].Text == ev.Text && when - Entries[0].When < DuplicateSpan)
                    return;
                Add(new ActivityEntry(when, ev.Text));
                return;
            }

            if (_rollups.TryGetValue(ev.Kind, out var r) && when - r.Start < RollupWindow && Entries.Contains(r.Entry))
            {
                if (!r.Names.Contains(ev.Subject, StringComparer.OrdinalIgnoreCase))
                {
                    r.Names.Add(ev.Subject);
                    r.Entry.Text = RollupText(ev.Kind, r.Names);
                }
                return;
            }

            var names = new List<string> { ev.Subject };
            var entry = new ActivityEntry(when, RollupText(ev.Kind, names));
            _rollups[ev.Kind] = (entry, when, names);
            Add(entry);
        }
        catch { /* the feed is informational; it must never disturb the UI */ }
    }

    private void Add(ActivityEntry entry)
    {
        Entries.Insert(0, entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(Entries.Count - 1);
    }
}
