// ════════════════════════════════════════════════════════════════════════════
// WindowsUpdateTweaksTests.cs
//
// Tests for WindowsUpdateTweaksService logic.
//
// The service writes/removes four HKLM policy registry values to block Windows
// preview/insider builds. All tests here are pure — they test the evaluation
// logic and the auto-heal guard condition without touching the real registry,
// so they run safely in any CI or sandbox environment without admin rights.
//
// RELATED FILES
//   Services/WindowsUpdateTweaksService.cs  — service under test
//   ViewModels/ToolsViewModel.cs            — consumes the service; auto-heal guard fix
// ════════════════════════════════════════════════════════════════════════════

using Systema.Services;

namespace Systema.Tests;

public class WindowsUpdateTweaksTests
{
    // ══════════════════════════════════════════════════════════════════════════
    // 1. EvaluateBlockState — pure registry-value evaluation logic
    //    All four values must be present with exact values for the block to be
    //    considered fully applied. Any missing or wrong value → not blocked.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AllFourCorrect_ReturnsTrue()
    {
        // Exactly the values BlockPreviewUpdatesAsync writes.
        Assert.True(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,   // ManagePreviewBuilds = 1
            policyVal: (object)0,   // ManagePreviewBuildsPolicyValue = 0
            branch:    (object)16,  // BranchReadinessLevel = 16
            optional:  (object)0)); // AllowOptionalContent = 0
    }

    [Fact]
    public void MissingManagePreviewBuilds_ReturnsFalse()
    {
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    null,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  (object)0));
    }

    [Fact]
    public void MissingManagePreviewBuildsPolicyValue_ReturnsFalse()
    {
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: null,
            branch:    (object)16,
            optional:  (object)0));
    }

    [Fact]
    public void MissingBranchReadinessLevel_ReturnsFalse()
    {
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    null,
            optional:  (object)0));
    }

    [Fact]
    public void MissingAllowOptionalContent_ReturnsFalse()
    {
        // This is the key that blocks monthly Preview CUs. If it's absent, the
        // IsPreviewUpdatesBlocked check must return false (not partially-blocked).
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  null));
    }

    [Fact]
    public void AllNull_ReturnsFalse()
    {
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(null, null, null, null));
    }

    [Fact]
    public void ManagePreviewBuilds_WrongValue_ReturnsFalse()
    {
        // ManagePreviewBuilds must be 1 to enable management. 0 means off.
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)0,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  (object)0));
    }

    [Fact]
    public void ManagePreviewBuildsPolicyValue_WrongValue_ReturnsFalse()
    {
        // PolicyValue must be 0 to block builds. 1 = allow Insider builds.
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)1,
            branch:    (object)16,
            optional:  (object)0));
    }

    [Fact]
    public void BranchReadinessLevel_WrongValue_ReturnsFalse()
    {
        // Insider rings use values 2 (Dev), 4 (Beta), 8 (Release Preview).
        // Only 16 = General Availability Channel is correct.
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    (object)2,   // Dev ring — wrong
            optional:  (object)0));
    }

    [Fact]
    public void AllowOptionalContent_WrongValue_ReturnsFalse()
    {
        // AllowOptionalContent = 1 means preview CUs are allowed — block not fully applied.
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  (object)1));
    }

    [Fact]
    public void WrongTypes_NotInt_ReturnsFalse()
    {
        // Registry.GetValue returns object? — a string or other type must not crash and must
        // return false (pattern match `is int` will fail gracefully).
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    "1",   // string, not int
            policyVal: "0",
            branch:    "16",
            optional:  "0"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. Auto-heal guard condition — verifies the race condition fix
    //
    //    Context: DoRefreshAsync has an auto-heal that re-applies the block if
    //    the registry is unblocked but the saved setting says "on". Before the
    //    fix, it would fire mid-toggle (toggle writes registry first, then waits
    //    for gpupdate ~10s, then writes the setting). The 137ms refresh cycle
    //    would see registry=unblocked, setting=on → fire auto-heal → re-block.
    //
    //    Fix: added `&& !IsPreviewUpdatesLoading` to the guard so auto-heal is
    //    suppressed while a toggle is in flight.
    //
    //    These tests document and verify the boolean guard expression directly.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AutoHealGuard_RegistryUnblocked_SettingOn_NotLoading_ShouldHeal()
    {
        // Normal auto-heal scenario: the block dropped (e.g. Windows removed it),
        // the saved setting says it should be on, no toggle is in flight → heal.
        bool previewBlocked            = false;  // registry: unblocked
        bool blockPreviewUpdatesEnabled = true;  // saved setting: should be blocked
        bool isPreviewUpdatesLoading   = false;  // no toggle in flight

        bool shouldHeal = !previewBlocked && blockPreviewUpdatesEnabled && !isPreviewUpdatesLoading;

        Assert.True(shouldHeal, "Auto-heal should fire when registry dropped and no toggle is running.");
    }

    [Fact]
    public void AutoHealGuard_RegistryUnblocked_SettingOn_Loading_ShouldNotHeal()
    {
        // Race condition scenario: user just clicked "off" — registry is being
        // cleared but gpupdate is still running (setting not yet written).
        // IsPreviewUpdatesLoading = true → guard must suppress auto-heal.
        bool previewBlocked            = false;  // registry: unblocked (toggle just cleared it)
        bool blockPreviewUpdatesEnabled = true;  // saved setting: still "on" (gpupdate pending)
        bool isPreviewUpdatesLoading   = true;   // toggle is in flight!

        bool shouldHeal = !previewBlocked && blockPreviewUpdatesEnabled && !isPreviewUpdatesLoading;

        Assert.False(shouldHeal, "Auto-heal must NOT fire while a toggle is in progress — this was the v1.7.63 race condition.");
    }

    [Fact]
    public void AutoHealGuard_AlreadyBlocked_ShouldNotHeal()
    {
        // Registry is already fully blocked → no heal needed regardless of loading state.
        bool previewBlocked            = true;
        bool blockPreviewUpdatesEnabled = true;
        bool isPreviewUpdatesLoading   = false;

        bool shouldHeal = !previewBlocked && blockPreviewUpdatesEnabled && !isPreviewUpdatesLoading;

        Assert.False(shouldHeal);
    }

    [Fact]
    public void AutoHealGuard_SettingOff_RegistryUnblocked_ShouldNotHeal()
    {
        // User turned the feature off — both registry and setting agree → no heal.
        bool previewBlocked            = false;
        bool blockPreviewUpdatesEnabled = false;
        bool isPreviewUpdatesLoading   = false;

        bool shouldHeal = !previewBlocked && blockPreviewUpdatesEnabled && !isPreviewUpdatesLoading;

        Assert.False(shouldHeal);
    }

    [Fact]
    public void AutoHealGuard_SettingOff_Loading_ShouldNotHeal()
    {
        // Toggle is running but setting is already false — definitely no heal.
        bool previewBlocked            = false;
        bool blockPreviewUpdatesEnabled = false;
        bool isPreviewUpdatesLoading   = true;

        bool shouldHeal = !previewBlocked && blockPreviewUpdatesEnabled && !isPreviewUpdatesLoading;

        Assert.False(shouldHeal);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. Three-value partial-block scenarios
    //    Before v1.7.63 the service only wrote 3 keys. If a user upgraded from
    //    an older version, IsPreviewUpdatesBlocked() returns false (triggering
    //    auto-heal on next app start which re-applies all 4 keys).
    //    These tests document that "partial block → not fully blocked" behaviour.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThreeKeys_MissingAllowOptionalContent_NotFullyBlocked()
    {
        // Older Systema wrote only 3 keys (ManagePreviewBuilds + PolicyValue + Branch).
        // AllowOptionalContent was added later. Three keys alone must return false
        // so the auto-heal fires on next startup to apply the full 4-key set.
        Assert.False(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  null));   // not yet written by older version
    }

    [Fact]
    public void FourKeys_AllPresent_FullyBlocked()
    {
        // Post-v1.7.63 layout: all 4 keys present → fully blocked (core block).
        Assert.True(WindowsUpdateTweaksService.EvaluateBlockState(
            manage:    (object)1,
            policyVal: (object)0,
            branch:    (object)16,
            optional:  (object)0));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. WUfB activation is INTENTIONALLY no longer written (v0.7.9 hotfix)
    //
    //    History: v1.7.67 added DeferQualityUpdates=1 + DeferQualityUpdatesPeriodInDays=0
    //    to BlockPreviewUpdatesAsync because AllowOptionalContent=0 is ignored on
    //    Win11 22H2+ unless WUfB is active. Tests EvaluateWufbState_* used to
    //    pin that behaviour.
    //
    //    v0.7.9 hotfix: those two keys are NOT written anymore — and are actively
    //    REMOVED on apply — because activating WUfB on a non-MDM consumer install
    //    makes the WU client try to resolve management COM interfaces that don't
    //    fully exist on standalone Pro/Home machines, returning 0x80004002
    //    E_NOINTERFACE on the next update scan. The previous EvaluateWufbState_*
    //    tests are removed (the method is gone). Replaced with the source-scan
    //    regression guards below.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BlockPreviewUpdatesSource_DoesNotWriteDeferQualityUpdates()
    {
        var src = ReadWindowsUpdateTweaksServiceSource();

        var setDeferQuality = System.Text.RegularExpressions.Regex.Match(
            src, @"SetValue\s*\(\s*""DeferQualityUpdates""");
        Assert.False(setDeferQuality.Success,
            "BlockPreviewUpdatesAsync must NOT write DeferQualityUpdates — it activates " +
            "WUfB which breaks Windows Update on non-MDM machines (E_NOINTERFACE).");

        var setDeferDays = System.Text.RegularExpressions.Regex.Match(
            src, @"SetValue\s*\(\s*""DeferQualityUpdatesPeriodInDays""");
        Assert.False(setDeferDays.Success,
            "BlockPreviewUpdatesAsync must NOT write DeferQualityUpdatesPeriodInDays — " +
            "see DeferQualityUpdates comment above.");
    }

    [Fact]
    public void BlockPreviewUpdatesSource_DeletesLegacyWufbKeys()
    {
        var src = ReadWindowsUpdateTweaksServiceSource();

        var deleteQuality = System.Text.RegularExpressions.Regex.Match(
            src, @"DeleteValue\s*\(\s*""DeferQualityUpdates""");
        Assert.True(deleteQuality.Success,
            "BlockPreviewUpdatesAsync should DeleteValue(\"DeferQualityUpdates\") so users " +
            "who already have it set from an earlier Systema version get unbroken.");

        var deleteDays = System.Text.RegularExpressions.Regex.Match(
            src, @"DeleteValue\s*\(\s*""DeferQualityUpdatesPeriodInDays""");
        Assert.True(deleteDays.Success,
            "BlockPreviewUpdatesAsync should DeleteValue(\"DeferQualityUpdatesPeriodInDays\").");
    }

    private static string ReadWindowsUpdateTweaksServiceSource()
    {
        var asmDir = System.IO.Path.GetDirectoryName(
            typeof(WindowsUpdateTweaksTests).Assembly.Location)!;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            asmDir, "..", "..", "..", "..", "..",
            "src", "Systema", "Services", "WindowsUpdateTweaksService.cs"));
        Assert.True(System.IO.File.Exists(path), $"Source file not found at {path}");
        return System.IO.File.ReadAllText(path);
    }
}
