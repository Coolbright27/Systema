using System;
using System.IO;
using Xunit;

namespace Systema.Tests;

/// <summary>
/// Auto-Pilot must only lock controls it actually manages ON THIS MACHINE. Locking one it skips
/// tells the user "Controlled by Auto-Pilot" when nothing is controlling it.
/// </summary>
public class AutoPilotScopeTests
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

    // Auto-Pilot skips the power plan entirely on laptops (two !HasBattery() gates in
    // DashboardViewModel), so greying Performance Mode there locked a control it never touches.
    [Fact]
    public void PerformanceModeIsOnlyLockedWhereAutoPilotManagesIt()
    {
        var vm = Read("src", "Systema", "ViewModels", "VisualViewModel.cs");
        Assert.Contains("IsPerformanceModeAutoPiloted", vm);
        Assert.Contains("_settings.AutoPilotModeEnabled && !_powerPlanService.HasBattery()", vm);

        var xaml = Read("src", "Systema", "Views", "VisualView.xaml");
        Assert.Contains("Binding IsPerformanceModeAutoPiloted", xaml);
    }

    // The lock has to re-evaluate when Auto-Pilot Mode is toggled, or the toggle stays greyed
    // until the tab is rebuilt.
    [Fact]
    public void TheLockRefreshesWhenAutoPilotModeChanges()
    {
        var vm = Read("src", "Systema", "ViewModels", "VisualViewModel.cs");
        int m = vm.IndexOf("private void OnAutoPilotModeChanged", StringComparison.Ordinal);
        Assert.True(m > 0);
        Assert.Contains("IsPerformanceModeAutoPiloted", vm[m..(m + 500)]);
    }

    // The Auto-Pilot power step itself must stay desktop-only.
    [Fact]
    public void AutoPilotStillSkipsThePowerPlanOnLaptops()
    {
        var dash = Read("src", "Systema", "ViewModels", "DashboardViewModel.cs");
        int step = dash.IndexOf("await _powerPlan.SetHighPerformanceAsync();", StringComparison.Ordinal);
        Assert.True(step > 0);
        Assert.Contains("!_powerPlan.HasBattery()", dash[Math.Max(0, step - 600)..step]);
    }
}
