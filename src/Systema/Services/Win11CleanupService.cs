// ════════════════════════════════════════════════════════════════════════════
// Win11CleanupService.cs  ·  Silences Windows 11 consumer nags + web search
// ════════════════════════════════════════════════════════════════════════════
//
// Two independent, fully-reversible cleanups — both written entirely under HKCU
// (no admin token required, so the toggles always succeed):
//
//   1. Consumer-content nags  → ContentDeliveryManager suggestions, lock-screen
//      "fun facts"/spotlight tips, "Finish setting up your device" (SCOOBE),
//      tips & tricks notifications, Start "recommendations", and OneDrive ads in
//      File Explorer. Makes the desktop calm/clean (macOS-style) instead of
//      constantly suggesting apps and content.
//
//   2. Web/Bing search in Start → DisableSearchBoxSuggestions policy + Bing
//      toggles so the Start menu only searches local apps/files, with no Bing
//      round-trip and no web clutter.
//
// Both are re-asserted on app launch (see App.xaml.cs) when their matching
// setting is on — that is the "reinforcement" so a Windows feature update can't
// quietly bring the nags back. Restore() puts every value back to the Windows
// default so an uninstall (or toggling off) leaves a clean system.
//
// RELATED FILES
//   ToolsViewModel.cs            — the two System Tweaks toggles
//   Views/ToolsView.xaml         — the two cards
//   SettingsService.cs           — DisableSuggestionsEnabled / DisableWebSearchEnabled
//   UninstallCleanupService.cs   — restores both on uninstall
// ════════════════════════════════════════════════════════════════════════════

using Microsoft.Win32;
using Systema.Core;

namespace Systema.Services;

public class Win11CleanupService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    private const string CdmKey    = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string EngageKey = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement";
    private const string AdvKey    = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string ExplorerPolicyKey = @"Software\Policies\Microsoft\Windows\Explorer";

    // Every ContentDeliveryManager value we drive to 0 to silence suggestions,
    // spotlight tips, pre-installed app pushes, and "tips & tricks" toasts.
    // All of these default to 1 (enabled) on a fresh Windows 11 install.
    private static readonly string[] CdmValues =
    {
        "ContentDeliveryAllowed",
        "FeatureManagementEnabled",
        "OemPreInstalledAppsEnabled",
        "PreInstalledAppsEnabled",
        "PreInstalledAppsEverEnabled",
        "SilentInstalledAppsEnabled",
        "SoftLandingEnabled",                 // tips & tricks notifications
        "RotatingLockScreenOverlayEnabled",   // lock-screen "fun facts"
        "SystemPaneSuggestionsEnabled",       // Start menu app suggestions
        "SubscribedContentEnabled",
        "SubscribedContent-310093Enabled",    // Windows welcome experience
        "SubscribedContent-338387Enabled",    // lock-screen spotlight tips
        "SubscribedContent-338388Enabled",    // Start suggestions
        "SubscribedContent-338389Enabled",    // Settings / notification tips
        "SubscribedContent-338393Enabled",    // suggested content in Settings
        "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled",
        "SubscribedContent-353698Enabled",    // timeline suggestions
    };

    // ════════════════════════════════════════════════════════════════════════
    //  #3 — Consumer-content nags
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when the representative nag values are all silenced.</summary>
    public bool IsConsumerContentDisabled()
    {
        try
        {
            using var cdm = Registry.CurrentUser.OpenSubKey(CdmKey);
            bool cdmOk =
                cdm?.GetValue("ContentDeliveryAllowed")        is int a && a == 0 &&
                cdm?.GetValue("SystemPaneSuggestionsEnabled")  is int s && s == 0 &&
                cdm?.GetValue("SubscribedContent-338393Enabled") is int c && c == 0;
            if (!cdmOk) return false;

            using var eng = Registry.CurrentUser.OpenSubKey(EngageKey);
            if (eng?.GetValue("ScoobeSystemSettingEnabled") is not int e || e != 0) return false;

            using var adv = Registry.CurrentUser.OpenSubKey(AdvKey);
            return adv?.GetValue("Start_IrisRecommendations") is int r && r == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Win11Cleanup", $"IsConsumerContentDisabled read failed: {ex.Message}");
            return false;
        }
    }

    public Task<TweakResult> DisableConsumerContentAsync() => Task.Run(() =>
    {
        try
        {
            using (var cdm = Registry.CurrentUser.CreateSubKey(CdmKey, writable: true))
            {
                if (cdm == null) return TweakResult.Fail("Could not open ContentDeliveryManager key.");
                foreach (var v in CdmValues)
                    cdm.SetValue(v, 0, RegistryValueKind.DWord);
            }
            using (var eng = Registry.CurrentUser.CreateSubKey(EngageKey, writable: true))
                eng?.SetValue("ScoobeSystemSettingEnabled", 0, RegistryValueKind.DWord);
            using (var adv = Registry.CurrentUser.CreateSubKey(AdvKey, writable: true))
            {
                adv?.SetValue("Start_IrisRecommendations",   0, RegistryValueKind.DWord); // Start "recommended" feed
                adv?.SetValue("ShowSyncProviderNotifications", 0, RegistryValueKind.DWord); // OneDrive ads in Explorer
            }

            Log.Info("Win11Cleanup", "Consumer-content nags disabled");
            return TweakResult.Ok("Windows suggestions, tips, spotlight, and setup nags are now off. " +
                                  "Sign out / restart for every surface to refresh.");
        }
        catch (Exception ex)
        {
            Log.Error("Win11Cleanup", "DisableConsumerContent failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    public Task<TweakResult> RestoreConsumerContentAsync() => Task.Run(() =>
    {
        try
        {
            // Windows default for every one of these values is 1 (enabled).
            using (var cdm = Registry.CurrentUser.CreateSubKey(CdmKey, writable: true))
            {
                if (cdm != null)
                    foreach (var v in CdmValues)
                        cdm.SetValue(v, 1, RegistryValueKind.DWord);
            }
            using (var eng = Registry.CurrentUser.CreateSubKey(EngageKey, writable: true))
                eng?.SetValue("ScoobeSystemSettingEnabled", 1, RegistryValueKind.DWord);
            using (var adv = Registry.CurrentUser.CreateSubKey(AdvKey, writable: true))
            {
                adv?.SetValue("Start_IrisRecommendations",     1, RegistryValueKind.DWord);
                adv?.SetValue("ShowSyncProviderNotifications", 1, RegistryValueKind.DWord);
            }

            Log.Info("Win11Cleanup", "Consumer-content nags restored to Windows defaults");
            return TweakResult.Ok("Windows suggestions and tips restored to default.");
        }
        catch (Exception ex)
        {
            Log.Error("Win11Cleanup", "RestoreConsumerContent failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    // ════════════════════════════════════════════════════════════════════════
    //  #4 — Web/Bing results in Start search
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>True when the Start-menu web/Bing results policy is in force.</summary>
    public bool IsWebSearchDisabled()
    {
        try
        {
            using var pol = Registry.CurrentUser.OpenSubKey(ExplorerPolicyKey);
            return pol?.GetValue("DisableSearchBoxSuggestions") is int v && v == 1;
        }
        catch (Exception ex)
        {
            Log.Warn("Win11Cleanup", $"IsWebSearchDisabled read failed: {ex.Message}");
            return false;
        }
    }

    public Task<TweakResult> DisableWebSearchAsync() => Task.Run(() =>
    {
        try
        {
            using (var pol = Registry.CurrentUser.CreateSubKey(ExplorerPolicyKey, writable: true))
            {
                if (pol == null) return TweakResult.Fail("Could not open Explorer policy key.");
                pol.SetValue("DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord);
            }
            using (var search = Registry.CurrentUser.CreateSubKey(SearchKey, writable: true))
            {
                search?.SetValue("BingSearchEnabled", 0, RegistryValueKind.DWord);
                search?.SetValue("CortanaConsent",    0, RegistryValueKind.DWord);
            }

            Log.Info("Win11Cleanup", "Start-menu web/Bing search disabled");
            return TweakResult.Ok("Start search now returns local apps and files only — no Bing/web results. " +
                                  "Restart Explorer (or sign out) for it to take effect.");
        }
        catch (Exception ex)
        {
            Log.Error("Win11Cleanup", "DisableWebSearch failed", ex);
            return TweakResult.FromException(ex);
        }
    });

    public Task<TweakResult> RestoreWebSearchAsync() => Task.Run(() =>
    {
        try
        {
            using (var pol = Registry.CurrentUser.OpenSubKey(ExplorerPolicyKey, writable: true))
            {
                if (pol != null)
                {
                    pol.DeleteValue("DisableSearchBoxSuggestions", throwOnMissingValue: false);
                    // Drop the policy key if it is now empty so GP doesn't treat it as managed.
                    if (pol.ValueCount == 0 && pol.SubKeyCount == 0)
                    {
                        try { Registry.CurrentUser.DeleteSubKey(ExplorerPolicyKey, throwOnMissingSubKey: false); }
                        catch (Exception ex) { Log.Warn("Win11Cleanup", $"Delete empty Explorer policy key failed: {ex.Message}"); }
                    }
                }
            }
            using (var search = Registry.CurrentUser.OpenSubKey(SearchKey, writable: true))
                search?.SetValue("BingSearchEnabled", 1, RegistryValueKind.DWord);

            Log.Info("Win11Cleanup", "Start-menu web/Bing search restored to default");
            return TweakResult.Ok("Start search web results restored to default.");
        }
        catch (Exception ex)
        {
            Log.Error("Win11Cleanup", "RestoreWebSearch failed", ex);
            return TweakResult.FromException(ex);
        }
    });
}
