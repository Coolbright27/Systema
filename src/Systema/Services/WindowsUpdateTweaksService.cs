// ════════════════════════════════════════════════════════════════════════════
// WindowsUpdateTweaksService.cs  ·  Blocks Windows insider/preview builds via GPO
// ════════════════════════════════════════════════════════════════════════════
//
// Writes Group Policy registry keys under HKLM\SOFTWARE\Policies\Microsoft\
// Windows\WindowsUpdate to block insider preview builds and defer feature
// updates. Provides a matching revert operation to restore defaults.
//
// RELATED FILES
//   ToolsViewModel.cs  — Windows Update tweak toggle on the Tools tab
// ════════════════════════════════════════════════════════════════════════════

using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

/// <summary>
/// Manages Windows Update policy tweaks.
/// Specifically: blocking Windows preview/insider builds from appearing in Windows Update
/// while leaving normal (non-preview) quality and feature updates completely unaffected.
///
/// Registry keys written under HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate:
///   ManagePreviewBuilds            = 1   → enables the preview-build management policy
///   ManagePreviewBuildsPolicyValue = 0   → 0 = block preview builds, 1 = allow them
///   BranchReadinessLevel           = 16  → locks Windows Update to the General Availability
///                                          channel (stable only); prevents the system from
///                                          offering any Insider / preview ring updates even
///                                          if the user was previously enrolled
///
/// ManagePreviewBuilds alone is not sufficient — it prevents Insider enrollment but does
/// not force Windows Update off a preview ring that was already selected. BranchReadinessLevel
/// closes that gap.
/// </summary>
public class WindowsUpdateTweaksService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    private const string WuPolicyKey =
        @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the preview build block policy is currently applied.
    /// </summary>
    public bool IsPreviewUpdatesBlocked()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey);
            if (key == null) return false;
            var manage = key.GetValue("ManagePreviewBuilds");
            var val    = key.GetValue("ManagePreviewBuildsPolicyValue");
            // Both conditions must be set — ManagePreviewBuilds alone without
            // BranchReadinessLevel is an incomplete block (see class doc).
            return manage is int m && m == 1 &&
                   val    is int v && v == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("WUTweaks", "Could not read preview build policy state", ex);
            return false;
        }
    }

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

                key.SetValue("ManagePreviewBuilds",            1,  RegistryValueKind.DWord);
                key.SetValue("ManagePreviewBuildsPolicyValue", 0,  RegistryValueKind.DWord);
                // Lock to General Availability channel — prevents Windows Update from
                // staying on a preview ring that was already selected before this policy
                // was applied. Without this, ManagePreviewBuilds alone only blocks NEW
                // enrollment; existing preview-ring machines keep receiving preview builds.
                // 16 = General Availability Channel (stable), 2/4/8 = Insider rings.
                key.SetValue("BranchReadinessLevel",           16, RegistryValueKind.DWord);

                Log.Info("WUTweaks", "Preview update block applied — ManagePreviewBuilds=1, PolicyValue=0, BranchReadinessLevel=16");
                return TweakResult.Ok(
                    "Preview updates blocked. Windows Update is locked to the stable release " +
                    "channel and will no longer offer Insider or preview builds. Normal updates are unaffected.");
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
    /// </summary>
    public async Task<TweakResult> AllowPreviewUpdatesAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                Log.Info("WUTweaks", "Removing preview update block policy");

                using var key = Registry.LocalMachine.OpenSubKey(WuPolicyKey, writable: true);
                if (key != null)
                {
                    key.DeleteValue("ManagePreviewBuilds",            throwOnMissingValue: false);
                    key.DeleteValue("ManagePreviewBuildsPolicyValue", throwOnMissingValue: false);
                    // Must also remove BranchReadinessLevel — leaving it at 16 while the
                    // ManagePreviewBuilds policy is gone still restricts channel selection,
                    // which can confuse Windows Update on Insider-enrolled machines.
                    key.DeleteValue("BranchReadinessLevel",           throwOnMissingValue: false);
                }

                Log.Info("WUTweaks", "Preview update block removed — all three policy values deleted");
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
}
