using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Every value is written to the ACTIVE plan, so a plan switch lands on an unconfigured plan and
/// parking silently reverts. Applying at startup and boot alone was not enough.
/// </summary>
public class CoreParkingWatchTests
{
    private static string Read(params string[] parts)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string p = Path.Combine(dir, Path.Combine(parts));
            if (File.Exists(p)) return File.ReadAllText(p);
            dir = Directory.GetParent(dir)?.FullName!;
        }
        throw new FileNotFoundException(string.Join('/', parts));
    }

    [Fact]
    public void PlanChangesReEnforceParking()
    {
        var svc = Read("src", "Systema", "Services", "CoreParkingService.cs");

        Assert.Contains("StartPlanWatch", svc);

        // Both triggers are needed. PowerModeChanged is prompt on plug/unplug but misses another
        // app swapping plans while on AC; the poll catches those.
        Assert.Contains("PowerModeChanged", svc);
        Assert.Contains("_planWatch", svc);

        // Windows switches the plan a moment AFTER the power event, so sampling immediately
        // would still read the old plan.
        int h = svc.IndexOf("OnPowerModeChanged", StringComparison.Ordinal);
        Assert.True(h > 0);
        Assert.Contains("Task.Delay", svc[h..(h + 800)]);
    }

    [Fact]
    public void TheWatchIsActuallyStartedAtLaunch()
    {
        var app = Read("src", "Systema", "App.xaml.cs");
        Assert.Contains("StartPlanWatch", app);

        // Gated on the live setting rather than on startup state, so switching the feature on
        // later does not require a restart to get plan-change enforcement.
        Assert.Contains("CoreParkingEnabled", app);
    }

    // The card claimed only "persists across restarts", which was true and incomplete.
    [Fact]
    public void TheCardDescribesPlanChangeEnforcement()
    {
        var xaml = Read("src", "Systema", "Views", "ToolsView.xaml");
        Assert.Contains("power plan changes", xaml);

        // House style: option copy must not use em dashes.
        int card = xaml.IndexOf("Puts idle CPU cores to sleep", StringComparison.Ordinal);
        Assert.True(card > 0, "core efficiency card copy not found");
        Assert.DoesNotContain("\u2014", xaml[card..(card + 700)]);
    }

    // The parking timers stay at Windows' defaults. Forcing "All possible" (2) on a halved
    // 5-second timer made cores park all at once and immediately unpark again, which is what
    // hitched games. These control how FAST cores park, never how many, so depth is unaffected.
    [Fact]
    public void ParkingTimersStayAtWindowsDefaults()
    {
        var svc = Read("src", "Systema", "Services", "CoreParkingService.cs");

        Assert.Contains("DecreaseIdeal        = 0", svc);
        Assert.Contains("DecreaseTimeDefault = 10", svc);

        // The aggressive values must not come back under any name.
        Assert.DoesNotContain("DecreaseAllPossible", svc);
        Assert.DoesNotContain("DecreaseTimeFast", svc);

        // Both must still be written, so machines carrying the old values get healed rather
        // than left on them forever.
        Assert.Contains("(CpDecreasePolicyGuid,  DecreaseIdeal", svc);
        Assert.Contains("(CpDecreaseTimeGuid,    DecreaseTimeDefault", svc);
    }

    // An OEM tuner that resets the parking timers while leaving min cores alone was invisible
    // to the old single-sentinel drift check, so the values stayed wiped until the next reboot.
    [Fact]
    public void DriftCheckCoversEverySettingNotJustMinCores()
    {
        var svc = Read("src", "Systema", "Services", "CoreParkingService.cs");
        int at = svc.IndexOf("private void CheckPlanChanged", StringComparison.Ordinal);
        Assert.True(at > 0);
        string body = svc[at..(at + 2600)];

        // It must walk the whole table rather than reading one sentinel back.
        Assert.Contains("foreach", body);
        Assert.Contains("ParkingSettings(floorPercent: 0)", body);

        // Both power sources, since OEM tools reset AC and DC independently.
        Assert.Contains("ac: true", body);
        Assert.Contains("ac: false", body);

        // A setting that cannot be written on this hardware reads back null. Treating that as
        // drift would re-apply every 15 seconds forever.
        Assert.Contains("liveAc.HasValue", body);
        Assert.Contains("liveDc.HasValue", body);
    }
}
