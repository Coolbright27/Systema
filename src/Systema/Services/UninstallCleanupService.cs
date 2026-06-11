// ════════════════════════════════════════════════════════════════════════════
// UninstallCleanupService.cs  ·  Restore all Windows settings on uninstall
// ════════════════════════════════════════════════════════════════════════════
//
// Called by the Inno Setup uninstaller via [UninstallRun] before files are
// deleted:
//
//   Systema.exe --cleanup
//
// Restores every setting Systema may have changed back to its Windows default.
// Each step is individually try/catch'd — a failure in one step never prevents
// subsequent steps from running (best-effort cleanup).
//
// RELATED FILES
//   App.xaml.cs                — detects --cleanup arg, calls RunCleanup(), exits
//   installer/systema_setup.iss — [UninstallRun] entry that spawns this path
// ════════════════════════════════════════════════════════════════════════════

using System.Text.Json;
using Microsoft.Win32;

namespace Systema.Services;

/// <summary>
/// Headless (no WPF UI) cleanup that runs during Inno Setup uninstall.
/// Instantiates only the services it needs; no ViewModel or window code is touched.
/// </summary>
public static class UninstallCleanupService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    public static void RunCleanup()
    {
        Log.Info("UninstallCleanup", "=== Uninstall cleanup starting ===");

        // 1. Telemetry — delete AllowTelemetry policy keys, restore feedback prefs,
        //                re-enable DiagTrack + dmwappushservice, re-enable CEIP tasks
        TryStep("Restore telemetry settings", () =>
            new TelemetryService().RestoreTelemetryAsync().GetAwaiter().GetResult());

        // 2. Visual effects — restore all 15 animation properties to Windows defaults
        TryStep("Restore animations to Windows defaults", () =>
            new AnimationService().ApplyWindowsDefault());

        // 3. Core parking — remove CPMINCORES overrides from all power schemes
        //    and delete the SystemaCoreParking scheduled task
        TryStep("Remove core parking enforcement", () =>
            new CoreParkingService().DisableForcedCoreParking().GetAwaiter().GetResult());

        // 4. Power plan — remove DC CPU cap (80%/99%) and set Balanced
        TryStep("Restore power plan to Balanced", () =>
        {
            var ps = new PowerPlanService();
            ps.RestoreMaxProcessorState();            // remove turbo/cap overrides
            ps.SetBalancedAsync().GetAwaiter().GetResult();
        });

        // 5. Fast Startup — re-enable hibernation + HiberbootEnabled=1
        TryStep("Re-enable Fast Startup", () =>
            new SystemStabilityService().EnableFastStartupAsync().GetAwaiter().GetResult());

        // 6. NTFS last-access timestamps — re-enable via fsutil
        TryStep("Re-enable NTFS last-access timestamps", () =>
            new SystemStabilityService().EnableNtfsLastAccessAsync().GetAwaiter().GetResult());

        // 6b. Engine responsiveness tweaks — restore Windows defaults for Foreground
        //     Priority Boost, Instant App Focus, and Instant Startup Apps.
        TryStep("Restore foreground priority boost", () =>
            new SystemStabilityService().DisableForegroundBoostAsync().GetAwaiter().GetResult());
        TryStep("Restore instant app focus", () =>
            new SystemStabilityService().DisableInstantAppFocusAsync().GetAwaiter().GetResult());
        TryStep("Restore startup app delay", () =>
            new SystemStabilityService().DisableInstantStartupAppsAsync().GetAwaiter().GetResult());

        // 7. Windows Update — remove ManagePreviewBuilds / BranchReadinessLevel policy
        TryStep("Remove Windows Update preview build block", () =>
            new WindowsUpdateTweaksService().AllowPreviewUpdatesAsync().GetAwaiter().GetResult());

        // Graphics tweaks (MPO / HAGS / windowed optimizations) are intentionally NOT
        // restored on uninstall — they're reflect-only mirrors of Windows' own settings,
        // so whatever the user has set is theirs to keep, not Systema's to undo.

        // 7b. Windows 11 cleanup — restore suggestions/nags and Start web search
        TryStep("Restore Windows suggestions and nags", () =>
            new Win11CleanupService().RestoreConsumerContentAsync().GetAwaiter().GetResult());
        TryStep("Restore Start web search", () =>
            new Win11CleanupService().RestoreWebSearchAsync().GetAwaiter().GetResult());

        // 8. DNS — restore all active adapters to DHCP (System Default)
        TryStep("Restore DNS to DHCP", () =>
        {
            var dhcp = DnsService.Profiles.FirstOrDefault(p => string.IsNullOrEmpty(p.Primary));
            if (dhcp != null)
                new DnsService().ApplyProfileAsync(dhcp).GetAwaiter().GetResult();
        });

        // 9. StartWithWindows — delete the Systema Task Scheduler logon task + Run key fallback
        TryStep("Remove StartWithWindows task", () =>
            new SettingsService().StartWithWindows = false);

        // 10. Game Boost crash-recovery snapshot — if the app was removed while boost was
        //     active (force-kill + uninstall), restore the saved pre-boost system state
        TryStep("Restore any in-progress game boost settings", RestoreGameBoostStateFromDisk);

        // 11. Systema registry key — delete HKCU\Software\Systema (settings, first-run marker)
        TryStep("Delete HKCU\\Software\\Systema", () =>
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Systema", throwOnMissingSubKey: false));

        Log.Info("UninstallCleanup", "=== Uninstall cleanup complete ===");
    }

    // ── Step runner ───────────────────────────────────────────────────────────

    private static void TryStep(string name, Action action)
    {
        try
        {
            action();
            Log.Info("UninstallCleanup", $"OK  — {name}");
        }
        catch (Exception ex)
        {
            Log.Warn("UninstallCleanup", $"SKIP — {name}: {ex.Message}");
        }
    }

    // ── Game Boost crash-recovery restore ─────────────────────────────────────

    /// <summary>
    /// Reads boost_state.json (written by GameBoosterService on boost activation as
    /// a write-ahead log). If the file is present it means the app was removed while
    /// a boost session was still active — we need to restore those settings manually.
    /// </summary>
    private static void RestoreGameBoostStateFromDisk()
    {
        var boostStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Systema", "boost_state.json");

        if (!File.Exists(boostStatePath))
        {
            Log.Info("UninstallCleanup", "No boost_state.json — nothing extra to restore");
            return;
        }

        Log.Info("UninstallCleanup", $"Found boost_state.json — restoring game boost settings");

        try
        {
            var json = File.ReadAllText(boostStatePath);
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            // Nagle algorithm — per-adapter TcpAckFrequency / TCPNoDelay
            RestoreRegistryList(root, "NagleRestore", Registry.LocalMachine);

            // NIC power saving — per-adapter PnPCapabilities
            RestoreRegistryList(root, "NicPowerRestore", Registry.LocalMachine);

            // Notifications (toasts)
            if (TryGetNullableInt(root, "NotificationsEnabled", out int notif))
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings", writable: true);
                key?.SetValue("NOC_GLOBAL_SETTING_TOASTS_ENABLED", notif, RegistryValueKind.DWord);
                Log.Info("UninstallCleanup", "  Notifications restored");
            }

            // Power plan (game booster may have switched to High Performance)
            if (root.TryGetProperty("PowerPlanGuid", out var ppProp) &&
                ppProp.ValueKind == JsonValueKind.String)
            {
                var guid = ppProp.GetString();
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("powercfg", $"/setactive {guid}")
                    { UseShellExecute = false, CreateNoWindow = true };
                    System.Diagnostics.Process.Start(psi)?.WaitForExit(10_000);
                    Log.Info("UninstallCleanup", $"  Power plan restored to {guid}");
                }
            }

            // Game Bar / DVR
            if (TryGetNullableInt(root, "AppCaptureEnabled", out int capture))
            {
                using var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\GameDVR", writable: true);
                k?.SetValue("AppCaptureEnabled", capture, RegistryValueKind.DWord);
            }
            if (TryGetNullableInt(root, "GameDvrEnabled", out int dvr))
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore", writable: true);
                k?.SetValue("GameDVR_Enabled", dvr, RegistryValueKind.DWord);
            }

            // Multimedia profile (SystemResponsiveness)
            if (TryGetNullableInt(root, "SystemResponsiveness", out int sr))
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                    writable: true);
                k?.SetValue("SystemResponsiveness", sr, RegistryValueKind.DWord);
                Log.Info("UninstallCleanup", "  SystemResponsiveness restored");
            }

            // Windows Search — if it was running before boost, restore it to Automatic
            if (root.TryGetProperty("SearchIndexingWasRunning", out var srProp) &&
                srProp.ValueKind == JsonValueKind.True)
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\WSearch", writable: true);
                k?.SetValue("Start", 2, RegistryValueKind.DWord);
                Log.Info("UninstallCleanup", "  WSearch re-enabled (was running before boost)");
            }

            // Clean up the snapshot file
            File.Delete(boostStatePath);
            Log.Info("UninstallCleanup", "  boost_state.json deleted");
        }
        catch (Exception ex)
        {
            Log.Warn("UninstallCleanup", $"RestoreGameBoostStateFromDisk failed: {ex.Message}");
        }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Iterates a JSON array of { Path, Name, Val? } registry restore entries and
    /// applies them: sets the DWORD if Val is non-null, deletes the value otherwise.
    /// </summary>
    private static void RestoreRegistryList(JsonElement root, string propertyName, RegistryKey hive)
    {
        if (!root.TryGetProperty(propertyName, out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return;

        int count = 0;
        foreach (var entry in list.EnumerateArray())
        {
            try
            {
                var path  = entry.GetProperty("Path").GetString();
                var name  = entry.GetProperty("Name").GetString();
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) continue;

                using var key = hive.OpenSubKey(path, writable: true);
                if (key == null) continue;

                if (entry.TryGetProperty("Val", out var valProp) &&
                    valProp.ValueKind == JsonValueKind.Number)
                    key.SetValue(name, valProp.GetInt32(), RegistryValueKind.DWord);
                else
                    key.DeleteValue(name, throwOnMissingValue: false);

                count++;
            }
            catch (Exception ex)
            {
                Log.Warn("UninstallCleanup", $"{propertyName} entry restore failed: {ex.Message}");
            }
        }
        if (count > 0)
            Log.Info("UninstallCleanup", $"  {propertyName}: {count} value(s) restored");
    }

    /// <summary>Returns true and the integer value if the JSON property is a non-null number.</summary>
    private static bool TryGetNullableInt(JsonElement root, string property, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(property, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Null)          return false;
        if (prop.ValueKind != JsonValueKind.Number)        return false;
        value = prop.GetInt32();
        return true;
    }
}
