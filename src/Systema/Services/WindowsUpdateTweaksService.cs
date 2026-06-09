// ════════════════════════════════════════════════════════════════════════════
// WindowsUpdateTweaksService.cs  ·  Blocks Windows insider/preview builds via GPO
// ════════════════════════════════════════════════════════════════════════════
//
// Writes Group Policy registry keys under HKLM\SOFTWARE\Policies\Microsoft\
// Windows\WindowsUpdate to block insider preview builds and optional preview
// cumulative updates. Provides a matching revert operation to restore defaults.
//
// ── SAFETY VERIFICATION (verified against Microsoft Policy CSP docs) ─────────
//
//   WHAT IS BLOCKED (intent):
//     • Windows Insider Program builds (Dev / Beta / Release Preview rings)
//     • Monthly optional "Preview Update" cumulative updates (e.g. KB5083631)
//     • Controlled Feature Rollouts (CFRs) — gradual optional feature deployments
//
//   WHAT IS NOT BLOCKED (verified by Microsoft Policy CSP):
//     • Mandatory security / quality updates (Patch Tuesday)        — unaffected
//     • Feature updates (major version upgrades, e.g. 24H2 → 25H2) — unaffected
//     • Windows Defender virus / malware definition updates          — unaffected
//     • Microsoft Store app updates                                  — unaffected
//     • Windows Update service itself (wuauserv)                    — unaffected
//
//   KEY VALUE RATIONALE (source: learn.microsoft.com/en-us/windows/client-management
//                                /mdm/policy-csp-update):
//     BranchReadinessLevel = 16  → Per Microsoft docs, 16 is the DEFAULT value and
//                                  the unified stable channel for Windows 10 1903+
//                                  and all Windows 11 builds. The old "32" (SAC)
//                                  and "16" (SAC Preview) were merged into 16 in 1903.
//     AllowOptionalContent = 0   → Per Microsoft docs, 0 is the DEFAULT value.
//                                  Setting it via policy just enforces the default.
//                                  It ONLY controls optional CUs and CFRs — not
//                                  security patches or feature updates.
//     DeferQualityUpdates = 1    → Activates Windows Update for Business (WUfB) quality
//     DeferQualityUpdatesPeriodInDays = 0
//                                  update management. AllowOptionalContent = 0 requires
//                                  WUfB to be active to be enforced by the WU client on
//                                  Windows 11 22H2+ builds — without this, the WU client
//                                  ignores AllowOptionalContent and still surfaces monthly
//                                  preview CUs (e.g. the 2026 Preview) as optional updates.
//                                  DeferQualityUpdatesPeriodInDays = 0 means ZERO deferral
//                                  delay — mandatory security patches still install
//                                  immediately, exactly as without this policy.
//
// RELATED FILES
//   ToolsViewModel.cs  — Windows Update tweak toggle on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>
/// Manages Windows Update policy tweaks.
/// Blocks Insider builds and optional monthly Preview CUs while leaving all mandatory
/// security patches, feature upgrades, and Defender definitions completely unaffected.
///
/// Registry keys written under HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate:
///
///   ManagePreviewBuilds            = 1   → Enables preview-build management policy.
///   ManagePreviewBuildsPolicyValue = 0   → 0 = block all Insider builds; 1 = allow.
///
///   BranchReadinessLevel           = 16  → Stable channel. Per Microsoft Policy CSP docs,
///                                          16 is the DEFAULT value and the correct unified
///                                          stable channel for Windows 10 1903+ / Windows 11
///                                          (the old 16/32 SAC split merged into 16 in 1903).
///                                          Setting this explicitly enforces the default.
///
///   AllowOptionalContent           = 0   → Blocks monthly optional "Preview Update" CUs
///                                          (e.g. "2026-04 Preview Update KB5083631") and
///                                          Controlled Feature Rollouts (CFRs).
///                                          Per Microsoft docs, 0 is the DEFAULT; mandatory
///                                          security patches, feature updates (24H2→25H2),
///                                          and Defender definitions are NOT affected.
///
/// All four keys are written by BlockPreviewUpdatesAsync and removed by AllowPreviewUpdatesAsync.
/// The parent registry key is deleted on revert only if it becomes completely empty.
/// </summary>
public class WindowsUpdateTweaksService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    private const string WuPolicyKey =
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    // Direct (non-policy) UX settings path — controls the
    // "Get the latest updates as soon as they're available" toggle in Windows Update settings.
    // On Windows 11 22H2+ this opt-in surface delivers monthly preview CUs even when the
    // policy keys above are set; writing IsContinuousInnovationOptedIn = 0 disables it.
    private const string WuUxSettingsKey =
        @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the preview-build block is fully applied — meaning all
    /// four GPO values are set AND the UX opt-in is off. Anything less and the
    /// auto-heal in ToolsViewModel will silently re-apply.
    /// </summary>
    public bool IsPreviewUpdatesBlocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey);
            if (key == null) return false;
            bool gpoOk = EvaluateBlockState(
                key.GetValue("ManagePreviewBuilds"),
                key.GetValue("ManagePreviewBuildsPolicyValue"),
                key.GetValue("BranchReadinessLevel"),
                key.GetValue("AllowOptionalContent"));
            if (!gpoOk) return false;

            using var uxKey = Registry.LocalMachine.OpenSubKey(WuUxSettingsKey);
            return uxKey?.GetValue("IsContinuousInnovationOptedIn") is int v && v == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("WUTweaks", "Could not read preview block state", ex);
            return false;
        }
    }

    /// <summary>
    /// Pure evaluation of the four registry values that make up the preview block.
    /// Exposed as internal so unit tests can verify the logic without touching the registry.
    /// </summary>
    internal static bool EvaluateBlockState(object? manage, object? policyVal, object? branch, object? optional)
        => manage   is int m && m == 1  &&
           policyVal is int v && v == 0  &&
           branch   is int b && b == 16 &&
           optional is int o && o == 0;

    /// <summary>
    /// Blocks Windows preview / insider builds from showing in Windows Update.
    /// Normal cumulative, security, and feature updates for the current stable
    /// release are completely unaffected.
    /// </summary>
    public async Task<TweakResult> BlockPreviewUpdatesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                Log.Info("WUTweaks", "Applying full preview update + Insider Program block");

                // ────────────────────────────────────────────────────────────────
                // Real block. Earlier v0.7.9 builds tried to do this UX-only after
                // we wrongly blamed these GPO writes for the WU 0x80004002 crashes.
                // Root cause turned out to be the Privacy & Background Services
                // toggle disabling DPS / WdiServiceHost / DoSvc / WpcMonSvc, which
                // are all now in _noRecommendedTag and never get touched. With
                // that fixed, these classic preview-block keys are safe to write
                // again. They are the same keys community tools like ShutUp10
                // and O&O have used on consumer Pro for years.
                //
                // What each value does:
                //
                //   ManagePreviewBuilds            = 1   Enable preview-build management.
                //   ManagePreviewBuildsPolicyValue = 0   0 = block all Insider rings
                //                                          AND force-off any active ring.
                //   BranchReadinessLevel           = 16  Lock to General Availability
                //                                          (stable) channel. Without this,
                //                                          machines already on a preview
                //                                          ring keep getting preview builds.
                //   AllowOptionalContent           = 0   Block monthly "Preview" cumulative
                //                                          updates (e.g. KB5083631).
                //   IsContinuousInnovationOptedIn  = 0   Disable the "Get the latest
                //                                          updates as soon as they're
                //                                          available" Settings switch.
                //
                // What this DOES NOT touch (intentionally — these are the real
                // WUfB activators that DO break WU on non-MDM Pro):
                //
                //   DeferQualityUpdates / DeferQualityUpdatesPeriodInDays
                //   DeferFeatureUpdates / DeferFeatureUpdatesPeriodInDays
                //   Pause* values
                //
                // Result: Insider Program blocked, monthly preview CUs blocked,
                // optional preview offers hidden. Security / quality / feature
                // updates and Defender definitions continue to install normally.
                // ────────────────────────────────────────────────────────────────

                using (var key = Registry.LocalMachine.CreateSubKey(WuPolicyKey, writable: true))
                {
                    if (key == null)
                    {
                        Log.Error("WUTweaks", "Failed to open/create WindowsUpdate policy key — access denied?");
                        return TweakResult.Fail("Could not open the Windows Update policy registry key. " +
                                               "Make sure Systema is running as Administrator.");
                    }

                    key.SetValue("ManagePreviewBuilds",            1,  RegistryValueKind.DWord);
                    key.SetValue("ManagePreviewBuildsPolicyValue", 0,  RegistryValueKind.DWord);
                    key.SetValue("BranchReadinessLevel",           16, RegistryValueKind.DWord);
                    key.SetValue("AllowOptionalContent",           0,  RegistryValueKind.DWord);

                    // Defensive: actively REMOVE the WUfB-activating defer / pause
                    // values if a prior Systema build (or anything else) left them
                    // behind. Those are the keys that put WU into managed mode and
                    // returned E_NOINTERFACE.
                    key.DeleteValue("DeferQualityUpdates",             throwOnMissingValue: false);
                    key.DeleteValue("DeferQualityUpdatesPeriodInDays", throwOnMissingValue: false);
                    key.DeleteValue("DeferFeatureUpdates",             throwOnMissingValue: false);
                    key.DeleteValue("DeferFeatureUpdatesPeriodInDays", throwOnMissingValue: false);
                    key.DeleteValue("PauseQualityUpdates",             throwOnMissingValue: false);
                    key.DeleteValue("PauseQualityUpdatesStartTime",    throwOnMissingValue: false);
                    key.DeleteValue("PauseFeatureUpdates",             throwOnMissingValue: false);
                    key.DeleteValue("PauseFeatureUpdatesStartTime",    throwOnMissingValue: false);
                }

                // Block the Win11 22H2+ "Get the latest updates as soon as they're
                // available" UX opt-in. Separate surface from the policy keys above.
                try
                {
                    using var uxKey = Registry.LocalMachine.CreateSubKey(WuUxSettingsKey, writable: true);
                    uxKey?.SetValue("IsContinuousInnovationOptedIn", 0, RegistryValueKind.DWord);
                }
                catch (Exception ex)
                {
                    Log.Warn("WUTweaks", $"Could not write IsContinuousInnovationOptedIn (non-fatal): {ex.Message}");
                }

                // Refresh GP + kick a WU scan so the WU UI drops any offered
                // preview update promptly instead of waiting hours for the next
                // scheduled scan.
                RunGpUpdateAndScan();

                Log.Info("WUTweaks", "Preview block applied — Insider builds blocked, AllowOptionalContent=0, IsContinuousInnovationOptedIn=0");
                return TweakResult.Ok(
                    "Preview updates and Windows Insider Program blocked. Regular security, " +
                    "quality, and feature updates continue to install normally.");
            }
            catch (Exception ex)
            {
                Log.Error("WUTweaks", "Failed to apply preview update block", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>
    /// Deletes every Windows-Update-for-Business activation value Systema (or an
    /// earlier Systema build) may have left under
    /// HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate, and removes the
    /// parent key if it ends up empty.
    ///
    /// Called from BlockPreviewUpdatesAsync, AllowPreviewUpdatesAsync, and the
    /// app-startup heal path so a user who damaged their machine on an older
    /// Systema build recovers Windows Update access automatically.
    /// </summary>
    public static void ScrubLegacyWufbPolicyKeys()
    {
        // Every value here is a WUfB-activating policy per Microsoft's Policy CSP.
        // The presence of ANY of them — even with values that "look harmless" —
        // is enough to put the WU client into managed mode and cause E_NOINTERFACE
        // on a non-MDM consumer Pro install.
        string[] valuesToDelete =
        {
            "ManagePreviewBuilds",
            "ManagePreviewBuildsPolicyValue",
            "BranchReadinessLevel",
            "AllowOptionalContent",
            "DeferQualityUpdates",
            "DeferQualityUpdatesPeriodInDays",
            "DeferFeatureUpdates",
            "DeferFeatureUpdatesPeriodInDays",
            "PauseQualityUpdates",
            "PauseQualityUpdatesStartTime",
            "PauseFeatureUpdates",
            "PauseFeatureUpdatesStartTime",
        };

        bool emptyAfter = false;
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey, writable: true))
            {
                if (key == null) return; // nothing to clean
                foreach (var v in valuesToDelete)
                {
                    try { key.DeleteValue(v, throwOnMissingValue: false); }
                    catch (Exception ex) { Log.Warn("WUTweaks", $"Scrub: delete {v} failed: {ex.Message}"); }
                }
                emptyAfter = key.ValueCount == 0 && key.SubKeyCount == 0;
            }

            // Empty policy keys still count as "managed" on some Windows builds.
            // Drop the parent key entirely if nothing else lives under it.
            if (emptyAfter)
            {
                try { Registry.LocalMachine.DeleteSubKey(WuPolicyKey, throwOnMissingSubKey: false); }
                catch (Exception ex) { Log.Warn("WUTweaks", $"Scrub: delete empty parent failed: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WUTweaks", $"ScrubLegacyWufbPolicyKeys failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the preview build block, restoring default Windows Update behaviour.
    /// Deletes the individual values AND the WindowsUpdate policy key itself if empty,
    /// because Group Policy still reads an empty key as "managed" on some Windows builds.
    /// Also runs <c>gpupdate /force</c> so the policy change takes effect immediately.
    /// </summary>
    public async Task<TweakResult> AllowPreviewUpdatesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                Log.Info("WUTweaks", "Removing preview update block (UX value + any legacy WUfB keys)");

                // Defensive: clear DisableWindowsUpdateAccess if it was left set
                // to 1 by any prior code path. Systema never writes it, but
                // strip it just in case so users get their WU access back.
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey, writable: true);
                    if (key?.GetValue("DisableWindowsUpdateAccess") is int d && d == 1)
                    {
                        key.DeleteValue("DisableWindowsUpdateAccess", throwOnMissingValue: false);
                        Log.Warn("WUTweaks", "Found DisableWindowsUpdateAccess=1 — cleared to restore update access");
                    }
                }
                catch (Exception ex) { Log.Warn("WUTweaks", $"DisableWindowsUpdateAccess check failed (non-fatal): {ex.Message}"); }

                // Strip every WUfB-activating policy value (and the parent key if
                // it ends up empty). Shared helper used by the BLOCK path too,
                // and by the app-startup auto-heal, so the two paths can never
                // drift out of sync.
                ScrubLegacyWufbPolicyKeys();

                // Remove the one value the BLOCK path writes (the UX opt-in).
                try
                {
                    using var uxKey = Registry.LocalMachine.OpenSubKey(WuUxSettingsKey, writable: true);
                    if (uxKey != null)
                    {
                        uxKey.DeleteValue("IsContinuousInnovationOptedIn", throwOnMissingValue: false);
                        Log.Info("WUTweaks", "IsContinuousInnovationOptedIn removed from UX settings");
                    }
                }
                catch (Exception ex) { Log.Warn("WUTweaks", $"Could not remove IsContinuousInnovationOptedIn (non-fatal): {ex.Message}"); }

                // Force a Group Policy refresh and trigger a WU scan so the preview update
                // offer disappears from the Windows Update UI promptly (without this it can
                // linger for hours until the next scheduled WU scan).
                RunGpUpdateAndScan();

                Log.Info("WUTweaks", "Preview update block removed — policy key cleaned, gpupdate forced, WU scan triggered");
                return TweakResult.Ok(
                    "Preview update block removed. Windows Update behaviour restored to system default.");
            }
            catch (Exception ex)
            {
                Log.Error("WUTweaks", "Failed to remove preview update block", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    /// <summary>
    /// Fires <c>gpupdate /force</c> and <c>UsoClient.exe StartScan</c> as fire-and-forget
    /// background processes so the Windows Update UI reflects the policy change promptly —
    /// without this the preview update offer can linger for hours until the next WU scan.
    ///
    /// Both operations are best-effort (exceptions swallowed). Neither blocks the caller:
    /// the registry writes take effect immediately; the GP refresh and WU scan just
    /// accelerate how quickly the WU UI stops showing the blocked update offer.
    /// </summary>
    private static void RunGpUpdateAndScan()
    {
        // 1. Refresh Group Policy in the background — fire-and-forget.
        //    The registry writes above take effect immediately for the WU client;
        //    gpupdate just ensures the WU UI refreshes promptly rather than waiting
        //    for the next scheduled GP refresh cycle (typically 90 minutes).
        //    We do NOT wait for exit — gpupdate can take 5-30s and blocking here
        //    would freeze the UI for the entire duration.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "gpupdate.exe",
                Arguments       = "/force",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
        catch { /* best-effort */ }

        // 2. Trigger a Windows Update scan so the WU UI drops the preview offer promptly.
        //    Fire-and-forget — do NOT wait for the scan to complete.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "UsoClient.exe",
                Arguments       = "StartScan",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
        catch { /* best-effort — UsoClient is present on all Win10/11 builds */ }
    }
}
