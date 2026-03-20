// ════════════════════════════════════════════════════════════════════════════
// BloatwareService.cs  ·  Detects and removes pre-installed Microsoft apps
// ════════════════════════════════════════════════════════════════════════════
//
// Maintains a curated catalogue of safe-to-remove UWP apps (AppxPackages).
// Every entry is verified against the installed package list before being
// shown to the user — nothing appears unless it's actually present.
//
// SAFETY RULES (enforced in the catalogue):
//   ✗  No package that is required for Xbox Live or the Microsoft Store
//   ✗  No package Windows itself depends on (VCLibs, UI.Xaml, etc.)
//   ✗  No productivity apps people commonly use (Calculator, Photos, Paint)
//   ✗  No Xbox Game Bar or Xbox identity packages
//
// REMOVAL METHOD
//   PowerShell: Get-AppxPackage -Name '{name}' | Remove-AppxPackage
//   This removes for the current user session. Most apps can be reinstalled
//   from the Microsoft Store if needed.
//
// RELATED FILES
//   Models/BloatwareEntry.cs    — data shape for a single detectable app
//   BloatwareViewModel.cs       — drives the App Cleanup tab
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using Systema.Core;
using Systema.Models;

namespace Systema.Services;

public class BloatwareService
{
    private static readonly LoggerService _log = LoggerService.Instance;

    // ── Catalogue ─────────────────────────────────────────────────────────────
    // Each entry: (PackageNames[], DisplayName, Description)
    // Multiple package names = alternate names for the same app across Windows versions.

    private record CatalogueEntry(string[] PackageNames, string DisplayName, string Description);

    private static readonly CatalogueEntry[] _catalogue =
    [
        new(["Microsoft.BingNews"],
            "Bing News",
            "Microsoft's news feed app. Easy to replace with any news website."),

        new(["Microsoft.BingWeather"],
            "Bing Weather",
            "Microsoft's weather app. Not needed if you check weather in a browser."),

        new(["Microsoft.MicrosoftSolitaireCollection"],
            "Solitaire Collection",
            "Pre-installed card games. Reinstallable from the Microsoft Store anytime."),

        new(["Microsoft.MicrosoftOfficeHub"],
            "Office Hub",
            "A launcher tile for Microsoft Office subscriptions. Not the Office apps themselves."),

        new(["Microsoft.WindowsFeedbackHub"],
            "Feedback Hub",
            "Sends usage feedback to Microsoft. Safe to remove on non-Insider builds."),

        new(["Microsoft.GetHelp"],
            "Get Help",
            "Microsoft's built-in support app. You can find help on the web instead."),

        new(["Microsoft.549981C3F5F10"],
            "Cortana",
            "Microsoft's voice assistant. Search and Start Menu still work without it."),

        new(["Microsoft.Microsoft3DViewer"],
            "3D Viewer",
            "Opens 3D model files. Most users never need this."),

        new(["Microsoft.MixedReality.Portal"],
            "Mixed Reality Portal",
            "Only needed for Windows Mixed Reality VR headsets. Safe to remove otherwise."),

        new(["Microsoft.YourPhone", "MicrosoftCorporationII.YourPhoneExperience"],
            "Phone Link",
            "Connects an Android phone to Windows. Remove if you don't use this feature."),

        new(["Microsoft.Getstarted"],
            "Tips",
            "A tutorial app for new Windows users. Safe to remove once you're familiar."),

        new(["Microsoft.ZuneVideo"],
            "Movies & TV",
            "Microsoft's video player. VLC or another free player is a great replacement."),

        new(["Microsoft.ZuneMusic"],
            "Groove Music",
            "Microsoft's music player — discontinued. Spotify or similar is recommended."),

        new(["Microsoft.People"],
            "People",
            "A contacts app. Not needed if you manage contacts through your browser or email app."),

        new(["Microsoft.Todos"],
            "Microsoft To Do",
            "Microsoft's task-list app. Remove if you use a different app or don't need it."),

        new(["Clipchamp.Clipchamp"],
            "Clipchamp",
            "Microsoft's video editor. Large download — safe to remove if you don't edit video."),

        new(["MicrosoftCorporationII.QuickAssist"],
            "Quick Assist",
            "A remote support tool. Only needed if you help others fix their PC remotely."),

        new(["Microsoft.WindowsMaps"],
            "Maps",
            "Microsoft's offline-capable maps app. Most people use Google Maps in a browser."),

        new(["Microsoft.PowerAutomateDesktop"],
            "Power Automate",
            "An advanced automation tool for power users. Safe to remove if unused."),

        new(["MSTeams"],
            "Microsoft Teams (pre-installed)",
            "The consumer version of Teams pre-pinned by Windows. Removable if unused."),
    ];

    // ── Scan ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns catalogue entries that are currently installed on this machine.
    /// Runs one PowerShell call to enumerate all AppxPackages, then cross-
    /// references the result against the catalogue.
    /// </summary>
    public async Task<List<BloatwareEntry>> ScanAsync()
    {
        _log.Info("BloatwareService", "Scanning for installed pre-installed apps...");

        HashSet<string> installed = await GetInstalledPackageNamesAsync();

        var found = new List<BloatwareEntry>();
        // Track display names already added to avoid duplicates (e.g. Phone Link with 2 pkg names)
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _catalogue)
        {
            // Check if any of the package names for this entry are installed
            string? matchedPkg = entry.PackageNames.FirstOrDefault(installed.Contains);
            if (matchedPkg == null) continue;
            if (!seenNames.Add(entry.DisplayName)) continue; // deduplicate

            found.Add(new BloatwareEntry
            {
                DisplayName = entry.DisplayName,
                Description = entry.Description,
                PackageName = matchedPkg,       // the one actually installed
                IsInstalled = true,
                IsSelected  = false,
            });
        }

        _log.Info("BloatwareService", $"Scan complete — {found.Count} pre-installed app(s) found.");
        return found;
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes a single app by its package name.
    /// Returns success/failure so the caller can report per-app status.
    /// </summary>
    public async Task<TweakResult> RemoveAsync(string packageName)
    {
        _log.Info("BloatwareService", $"Removing: {packageName}");

        return await Task.Run(() =>
        {
            try
            {
                using var ps = new Process();
                ps.StartInfo = new ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    Arguments              = $"-NonInteractive -NoProfile -Command " +
                                             $"\"Get-AppxPackage -Name '{packageName}' | Remove-AppxPackage\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                ps.Start();
                // Read both streams concurrently to avoid pipe-buffer deadlock
                var stdoutTask = ps.StandardOutput.ReadToEndAsync();
                var stderrTask = ps.StandardError.ReadToEndAsync();
                ps.WaitForExit();
                string err = stderrTask.GetAwaiter().GetResult();
                string out_ = stdoutTask.GetAwaiter().GetResult();

                if (ps.ExitCode == 0)
                {
                    _log.Info("BloatwareService", $"Removed OK: {packageName}");
                    return TweakResult.Ok($"Removed: {packageName}");
                }
                else
                {
                    var msg = (string.IsNullOrWhiteSpace(err) ? out_ : err).Trim();
                    _log.Warn("BloatwareService", $"Remove failed ({packageName}): {msg}");
                    return TweakResult.Fail(msg);
                }
            }
            catch (Exception ex)
            {
                _log.Error("BloatwareService", $"Remove exception ({packageName})", ex);
                return TweakResult.FromException(ex);
            }
        });
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static async Task<HashSet<string>> GetInstalledPackageNamesAsync()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ps = new Process();
            ps.StartInfo = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                // -User $env:USERNAME ensures we enumerate the current user's packages
                // even when the process is running elevated (without this, Get-AppxPackage
                // can return provisioned/system packages instead of the user's installed set).
                Arguments              = "-NonInteractive -NoProfile -Command \"Get-AppxPackage -User $env:USERNAME | Select-Object -ExpandProperty Name\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            ps.Start();
            var stdoutTask = ps.StandardOutput.ReadToEndAsync();
            var stderrTask = ps.StandardError.ReadToEndAsync();
            ps.WaitForExit();
            string output = await stdoutTask;
            await stderrTask; // drain

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                names.Add(line.Trim());
        }
        catch { }
        return names;
    }
}
