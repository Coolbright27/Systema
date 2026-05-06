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
    /// Returns true only when the full preview-build block policy is applied.
    /// All six values must be present: ManagePreviewBuilds, ManagePreviewBuildsPolicyValue,
    /// BranchReadinessLevel, AllowOptionalContent, DeferQualityUpdates, AND
    /// DeferQualityUpdatesPeriodInDays.
    ///
    /// AllowOptionalContent=0 is required to suppress monthly "Preview" cumulative updates
    /// (e.g. "2026-04 Preview Update KB5083631") which are NOT Insider builds and are
    /// NOT blocked by the other four keys alone. However, on Windows 11 22H2+ the WU client
    /// ignores AllowOptionalContent unless Windows Update for Business (WUfB) quality update
    /// management is also active — hence DeferQualityUpdates=1 (WUfB enabled) and
    /// DeferQualityUpdatesPeriodInDays=0 (zero deferral, no delay to security patches).
    /// </summary>
    public bool IsPreviewUpdatesBlocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey);
            if (key == null) return false;
            return EvaluateBlockState(
                       key.GetValue("ManagePreviewBuilds"),
                       key.GetValue("ManagePreviewBuildsPolicyValue"),
                       key.GetValue("BranchReadinessLevel"),
                       key.GetValue("AllowOptionalContent")) &&
                   EvaluateWufbState(
                       key.GetValue("DeferQualityUpdates"),
                       key.GetValue("DeferQualityUpdatesPeriodInDays"));
        }
        catch (Exception ex)
        {
            Log.Warn("WUTweaks", "Could not read preview build policy state", ex);
            return false;
        }
    }

    /// <summary>
    /// Pure evaluation of the four registry values that make up the core preview block.
    /// Exposed as internal so unit tests can verify the logic without touching the registry.
    /// </summary>
    internal static bool EvaluateBlockState(object? manage, object? policyVal, object? branch, object? optional)
        => manage   is int m && m == 1  &&
           policyVal is int v && v == 0  &&
           branch   is int b && b == 16 &&
           optional is int o && o == 0;

    /// <summary>
    /// Pure evaluation of the two Windows Update for Business (WUfB) registry values
    /// that activate WUfB quality update management.
    ///
    /// AllowOptionalContent=0 is only enforced by the WU client when WUfB is active.
    /// DeferQualityUpdates=1 activates it; DeferQualityUpdatesPeriodInDays=0 means zero
    /// deferral delay — mandatory security patches still install immediately.
    ///
    /// Exposed as internal so unit tests can verify the logic without touching the registry.
    /// </summary>
    internal static bool EvaluateWufbState(object? deferQuality, object? deferDays)
        => deferQuality is int q && q == 1 &&
           deferDays   is int d && d == 0;

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
                Log.Info("WUTweaks", "Applying preview update block policy");

                using var key = Registry.LocalMachine.CreateSubKey(WuPolicyKey, writable: true);
                if (key == null)
                {
                    Log.Error("WUTweaks", "Failed to open/create WindowsUpdate policy key — access denied?");
                    return TweakResult.Fail("Could not open the Windows Update policy registry key. " +
                                           "Make sure Systema is running as Administrator.");
                }

                // Blocks Insider Program enrollment and forces off any active insider ring.
                key.SetValue("ManagePreviewBuilds",            1,  RegistryValueKind.DWord);
                key.SetValue("ManagePreviewBuildsPolicyValue", 0,  RegistryValueKind.DWord);

                // Locks Windows Update to the General Availability channel (stable only).
                // Without this, ManagePreviewBuilds alone only blocks NEW enrollment;
                // machines already on a preview ring keep receiving preview builds.
                // 16 = General Availability Channel (stable); 2/4/8 = Insider rings.
                key.SetValue("BranchReadinessLevel",           16, RegistryValueKind.DWord);

                // Blocks monthly "Preview" cumulative updates (e.g. "2026-04 Preview Update
                // KB5083631"). These are NOT Insider builds — they are optional pre-Patch-Tuesday
                // releases offered to all Windows users via the "Get latest updates as soon as
                // they're available" feature. The above three keys do NOT suppress these.
                // 0 = block all optional/preview content; 1 = allow (Windows default).
                key.SetValue("AllowOptionalContent",           0,  RegistryValueKind.DWord);

                // Activate Windows Update for Business (WUfB) quality update management.
                // On Windows 11 22H2+ the WU client ignores AllowOptionalContent = 0 unless
                // WUfB is active — without these two keys the "2026 Preview" and similar
                // optional preview CUs still surface in Windows Update even though
                // AllowOptionalContent is set.
                // DeferQualityUpdates = 1              → enables WUfB quality update control
                // DeferQualityUpdatesPeriodInDays = 0  → zero deferral: no delay on security
                //                                        patches (installs immediately as normal)
                key.SetValue("DeferQualityUpdates",              1, RegistryValueKind.DWord);
                key.SetValue("DeferQualityUpdatesPeriodInDays",  0, RegistryValueKind.DWord);

                // Block the "Get the latest updates as soon as they're available" UX opt-in.
                // On Windows 11 22H2+ this is a separate delivery surface that is NOT controlled
                // by the four policy keys above. IsContinuousInnovationOptedIn = 0 disables it
                // at the machine level. Non-fatal if the key or path doesn't exist.
                try
                {
                    using var uxKey = Registry.LocalMachine.CreateSubKey(WuUxSettingsKey, writable: true);
                    uxKey?.SetValue("IsContinuousInnovationOptedIn", 0, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Log.Warn("WUTweaks", $"Could not write IsContinuousInnovationOptedIn (non-fatal): {ex.Message}"); }

                // Force a Group Policy refresh and trigger a WU scan so the preview update
                // offer disappears from the Windows Update UI promptly (without this it can
                // linger for hours until the next scheduled WU scan).
                RunGpUpdateAndScan();

                Log.Info("WUTweaks", "Preview update block applied — ManagePreviewBuilds=1, PolicyValue=0, BranchReadinessLevel=16, AllowOptionalContent=0, DeferQualityUpdates=1, DeferQualityUpdatesPeriodInDays=0, IsContinuousInnovationOptedIn=0");
                return TweakResult.Ok(
                    "Preview updates blocked. Insider builds and monthly preview cumulative updates " +
                    "are now suppressed. Normal security and cumulative updates are unaffected.");
            }
            catch (Exception ex)
            {
                Log.Error("WUTweaks", "Failed to apply preview update block", ex);
                return TweakResult.FromException(ex);
            }
        });
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
                Log.Info("WUTweaks", "Removing preview update block policy");

                // Track whether the parent key becomes empty BEFORE releasing the handle.
                // We delete the parent key AFTER the using block ends so we never call
                // key.Close() manually inside a using scope (causes a double-dispose and
                // obscures the real execution order).
                bool deleteParentKey = false;

                using (var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey, writable: true))
                {
                    if (key != null)
                    {
                        // Remove all values we wrote.
                        key.DeleteValue("ManagePreviewBuilds",            throwOnMissingValue: false);
                        key.DeleteValue("ManagePreviewBuildsPolicyValue", throwOnMissingValue: false);
                        key.DeleteValue("BranchReadinessLevel",           throwOnMissingValue: false);
                        key.DeleteValue("AllowOptionalContent",           throwOnMissingValue: false);
                        key.DeleteValue("DeferQualityUpdates",            throwOnMissingValue: false);
                        key.DeleteValue("DeferQualityUpdatesPeriodInDays",throwOnMissingValue: false);

                        // Safety guard: also clear DisableWindowsUpdateAccess if it was left set
                        // to 1 by any code path. This value blocks ALL Windows Update access.
                        // Systema never sets it, but remove it defensively to ensure updates work.
                        var disableAccess = key.GetValue("DisableWindowsUpdateAccess");
                        if (disableAccess is int d && d == 1)
                        {
                            key.DeleteValue("DisableWindowsUpdateAccess", throwOnMissingValue: false);
                            Log.Warn("WUTweaks", "Found DisableWindowsUpdateAccess=1 — cleared to restore update access");
                        }

                        // If the key is now empty, mark it for deletion after we release the handle.
                        // An empty policy key can still be read as "managed" by the WU client.
                        deleteParentKey = key.ValueCount == 0 && key.SubKeyCount == 0;
                    }
                } // key handle released here — safe to delete the parent key now

                // Remove the UX opt-in value we wrote during blocking.
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

                if (deleteParentKey)
                {
                    // Wrap separately so a failure here (e.g. another process has the key open)
                    // does NOT cause AllowPreviewUpdatesAsync to report failure — the values
                    // are already gone and the policy is lifted regardless.
                    try { Registry.LocalMachine.DeleteSubKey(WuPolicyKey, throwOnMissingSubKey: false); }
                    catch (Exception ex) { Log.Warn("WUTweaks", $"Could not remove empty policy key (non-fatal): {ex.Message}"); }
                }

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
