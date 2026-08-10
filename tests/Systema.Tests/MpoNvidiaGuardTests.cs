// MpoNvidiaGuardTests.cs
//
// Regression guard for the NVIDIA VSync bug fixed in v0.7.278.
//
// Auto-Pilot disabled Multi-Plane Overlay on EVERY machine
// (HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5), with no GPU-vendor check. On AMD and
// Intel that's Microsoft's own documented fix for driver flicker. On NVIDIA it is the opposite of
// a fix: MPO is how the driver hands a borderless game an Independent Flip, so removing it makes
// DWM composite instead, the game's own VSync setting stops controlling presentation, and the user
// gets tearing that no in-game toggle can cure.
//
// It was also self-healing in the wrong direction. Two paths kept putting it back:
//   • the startup seed in App.xaml.cs adopted a live OverlayTestMode=5 as "user intent"
//   • ReinforceGraphicsFromIntent re-applied that intent whenever a driver update reset the value
// so a user who fixed it in the registry found it undone on the next launch.
//
// Confirmed on the user's Dell Precision 5560 (NVIDIA T1200) on 2026-08-09: OverlayTestMode = 0x5.
//
// Source-scan (not reflection) because Application Control blocks loading Systema.dll into the
// test host on this machine — same constraint as the other *SourceTests.

using System.IO;
using System.Linq;
using Xunit;

namespace Systema.Tests;

public class MpoNvidiaGuardTests
{
    private static string RepoRoot()
    {
        var asmDir = Path.GetDirectoryName(typeof(MpoNvidiaGuardTests).Assembly.Location)!;
        // tests/Systema.Tests/bin/<Cfg>/<TFM>/  →  ../../../../../
        return Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."));
    }

    private static string Read(params string[] parts)
    {
        var path = Path.GetFullPath(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
        Assert.True(File.Exists(path), $"Expected file not found: {path}");
        return File.ReadAllText(path);
    }

    private static string Dashboard() => Read("src", "Systema", "ViewModels", "DashboardViewModel.cs");
    private static string Graphics()  => Read("src", "Systema", "Services", "GraphicsTweaksService.cs");
    private static string App()       => Read("src", "Systema", "App.xaml.cs");

    [Fact]
    public void TheVendorGateExists()
    {
        var src = Graphics();
        Assert.Contains("public bool IsMpoAutoDisableUnsafe()", src);
        Assert.Contains("DetectNvidiaAdapters().Count > 0", src);
    }

    [Fact]
    public void AutoPilot_NeverDisablesMpoWithoutCheckingTheVendor()
    {
        var src = Dashboard();

        // Every automatic call must be guarded. The bug was a bare
        // `if (!_graphics.IsMpoDisabled()) _graphics.SetMpoDisabled(true);` in the Optimize pass.
        int idx = src.IndexOf("_graphics.SetMpoDisabled(true)", System.StringComparison.Ordinal);
        Assert.True(idx > 0, "expected Auto-Pilot to still manage MPO");

        // The guard must appear before the first enabling call.
        int gate = src.IndexOf("IsMpoAutoDisableUnsafe()", System.StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < idx,
            "Auto-Pilot disables MPO before checking IsMpoAutoDisableUnsafe — this is the NVIDIA VSync bug");
    }

    [Fact]
    public void AutoPilot_RestoresMpoOnNvidia()
    {
        // Auto-Pilot owns this value, so skipping is not enough: machines it already broke have to
        // be healed, otherwise the tearing outlives the fix.
        Assert.Contains("_graphics.SetMpoDisabled(false)", Dashboard());
    }

    [Fact]
    public void StartupSeed_DoesNotAdoptADisabledMpoOnNvidia()
    {
        var src = App();

        // The old form adopted it unconditionally, which promoted Systema's own write to "user intent".
        Assert.DoesNotContain("if (graphicsTweaks.IsMpoDisabled())      settingsService.GraphicsMpoDisabled     = true;", src);
        Assert.Contains("IsMpoDisabled() && !graphicsTweaks.IsMpoAutoDisableUnsafe()", src);
    }

    [Fact]
    public void Reinforcement_DoesNotReapplyDisabledMpoOnNvidia()
    {
        var src = Graphics();
        Assert.Contains("mpoDisabled && !IsMpoDisabled() && !IsMpoAutoDisableUnsafe()", src);
    }
}
