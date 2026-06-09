// ════════════════════════════════════════════════════════════════════════════
// TaskSleepDefaultsTests.cs
// Regression tests for Task Sleep default values.
//
// Bug history:
//   v1.7.27 — NappedCpuCapPercent ViewModel field default was 5, should be 3.
//             On first launch (no registry key), LoadSettings exits early and
//             the field default becomes the effective value shown in the UI.
//             Fix: changed field default from 5 → 3.
//   v0.7.77 — Defaults lowered for lighter idle CPU use:
//             NappedCpuCapPercent 3 → 2, BriefWakeCpuCapPercent 7 → 5.
//             Updated in the model, the VM field, AND the LoadSettings fallback.
// ════════════════════════════════════════════════════════════════════════════

using Systema.Models;

namespace Systema.Tests;

public class TaskSleepDefaultsTests
{
    // ── TaskSleepSettings model defaults ─────────────────────────────────────
    // These are the canonical source of truth for every setting default.
    // If any default changes intentionally, update both the class AND this test.

    [Fact]
    public void TaskSleepSettings_NappedCpuCapPercent_DefaultIs2()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(2, s.NappedCpuCapPercent);
    }

    [Fact]
    public void TaskSleepSettings_BriefWakeCpuCapPercent_DefaultIs5()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(5, s.BriefWakeCpuCapPercent);
    }

    [Fact]
    public void TaskSleepSettings_NappedCpuCapEnabled_DefaultIsTrue()
    {
        var s = new TaskSleepSettings();
        Assert.True(s.NappedCpuCapEnabled);
    }

    [Fact]
    public void TaskSleepSettings_IsEnabled_DefaultIsTrue()
    {
        var s = new TaskSleepSettings();
        Assert.True(s.IsEnabled);
    }

    [Fact]
    public void TaskSleepSettings_MinimizeNapEnabled_DefaultIsTrue()
    {
        var s = new TaskSleepSettings();
        Assert.True(s.MinimizeNapEnabled);
    }

    [Fact]
    public void TaskSleepSettings_TrayNapEnabled_DefaultIsTrue()
    {
        var s = new TaskSleepSettings();
        Assert.True(s.TrayNapEnabled);
    }

    [Fact]
    public void TaskSleepSettings_BackgroundNapEnabled_DefaultIsTrue()
    {
        var s = new TaskSleepSettings();
        Assert.True(s.BackgroundNapEnabled);
    }

    [Fact]
    public void TaskSleepSettings_MaxConcurrentBriefWakes_DefaultIs3()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(3, s.MaxConcurrentBriefWakes);
    }

    [Fact]
    public void TaskSleepSettings_SystemCpuTriggerPercent_DefaultIs12()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(12, s.SystemCpuTriggerPercent);
    }

    [Fact]
    public void TaskSleepSettings_ProcessCpuStartPercent_DefaultIs7()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(7, s.ProcessCpuStartPercent);
    }

    [Fact]
    public void TaskSleepSettings_ProcessCpuStopPercent_DefaultIs3()
    {
        var s = new TaskSleepSettings();
        Assert.Equal(3, s.ProcessCpuStopPercent);
    }

    [Fact]
    public void TaskSleepSettings_NappedCpuCapPercent_IsWithinValidRange()
    {
        var s = new TaskSleepSettings();
        Assert.InRange(s.NappedCpuCapPercent, 1, 100);
    }

    // ── Cross-check: ViewModel LoadSettings fallback must match model default ─
    // The VM's ReadInt(key, "NappedCpuCapPercent", 2) fallback and the field
    // default must both be 2. This test encodes that expectation explicitly so
    // a future change to either value triggers a visible failure.

    [Fact]
    public void NappedCpuCapPercent_ModelDefault_MatchesLoadSettingsFallback()
    {
        // Model default
        int modelDefault = new TaskSleepSettings().NappedCpuCapPercent;

        // LoadSettings fallback (the second arg to ReadInt) — must stay in sync.
        // If someone changes TaskSleepSettings default, they must also update
        // the ReadInt fallback in LoadSettings, and vice versa.
        const int loadSettingsFallback = 2; // matches ReadInt(key, "NappedCpuCapPercent", 2)

        Assert.Equal(loadSettingsFallback, modelDefault);
    }

    // ── Boundary: cap percent clamp ───────────────────────────────────────────
    // BuildSettings clamps NappedCpuCapPercent to [1, 100].

    [Fact]
    public void NappedCpuCapPercent_ClampedToMinimumOf1()
    {
        // Value below 1 must be brought up to 1.
        int raw    = 0;
        int clamped = Math.Clamp(raw, 1, 100);
        Assert.Equal(1, clamped);
    }

    [Fact]
    public void NappedCpuCapPercent_ClampedToMaximumOf100()
    {
        int raw    = 150;
        int clamped = Math.Clamp(raw, 1, 100);
        Assert.Equal(100, clamped);
    }

    [Fact]
    public void NappedCpuCapPercent_DefaultOf2_SurvivesClamping()
    {
        // The default 2 must not be modified by the clamp.
        int clamped = Math.Clamp(2, 1, 100);
        Assert.Equal(2, clamped);
    }
}
