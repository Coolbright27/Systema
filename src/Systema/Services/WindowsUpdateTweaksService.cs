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
/// Registry: HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate
///   ManagePreviewBuilds         = 1  → policy is active (management enabled)
///   ManagePreviewBuildsPolicyValue = 0  → 0 = block preview builds, 1 = allow them
///
/// This is the official Microsoft Group Policy mechanism for controlling preview builds.
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

                key.SetValue("ManagePreviewBuilds",            1, RegistryValueKind.DWord);
                key.SetValue("ManagePreviewBuildsPolicyValue", 0, RegistryValueKind.DWord);

                Log.Info("WUTweaks", "Preview update block applied — ManagePreviewBuilds=1, PolicyValue=0");
                return TweakResult.Ok(
                    "Preview updates blocked. Windows Update will no longer offer or install " +
                    "Windows Insider / preview builds. Normal updates are unaffected.");
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
                }

                Log.Info("WUTweaks", "Preview update block removed — Windows Update policy restored to default");
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
