// ════════════════════════════════════════════════════════════════════════════
// UpdateService.cs  ·  Fully-automatic silent updater
// ════════════════════════════════════════════════════════════════════════════
//
// Lifecycle
//   1. On startup (20 s delay): check GitHub for a newer release.
//   2. If found → immediately start downloading in the background.
//      Status transitions: "Found → Downloading X% → Ready to install".
//   3. Once downloaded, wait until CPU has been below 60 % for 5 consecutive
//      minutes AND no game session is active, then install silently.
//   4. Re-check every 2 hours while the app is running.
//   5. Manual "Check Now" / "Install Now" buttons in Settings bypass the
//      schedule / CPU gate respectively.
//
// Download-then-install split
//   _pendingInstall       — the update that was found (set by DoCheckAsync)
//   _downloadedPath       — path to the downloaded .exe (set by BeginDownloadAsync)
//   IsDownloading         — true while the HTTP download is in progress
//   IsReadyToInstall      — true when the file is on disk and ready to launch
//
// Pre-release rule
//   IsPreReleaseBuild = false  → only stable GitHub releases are ever seen.
//   IsPreReleaseBuild = true   → pre-releases are included (beta channel).
//
// RELATED FILES
//   ViewModels/SettingsViewModel.cs  — subscribes to all events; exposes UI state
//   App.xaml.cs                      — calls StartAutoUpdate(), wires ShutdownRequested
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Systema.Services;

public class UpdateService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    private const string Owner             = "Coolbright27";
    private const string Repo              = "Systema";
    public  const bool   IsPreReleaseBuild = false;

    private static readonly string ApiUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases";

    /// <summary>How often to re-check while the app is running (hours).</summary>
    private const double CheckIntervalHours = 2.0;

    /// <summary>CPU must stay below this % to count as "idle".</summary>
    private const float CpuIdleThreshold = 60f;

    /// <summary>
    /// How many consecutive 30-second idle samples are needed before we install.
    /// 10 × 30 s = 5 minutes.
    /// </summary>
    private const int IdleSamplesRequired = 10;

    /// <summary>
    /// How many times the idle counter can be reset by CPU spikes before we
    /// give up and wait <see cref="SpikeBackoffMinutes"/> minutes.
    /// </summary>
    private const int MaxSpikeResetsBeforeBackoff = 3;

    /// <summary>How long to back off after too many CPU spikes (minutes).</summary>
    private const int SpikeBackoffMinutes = 15;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires (from a background thread) whenever the status text changes.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Fires (from a background thread) when an update is found or dismissed.</summary>
    public event Action<bool>? UpdateAvailableChanged;

    /// <summary>Fires when the download starts (true) or finishes/fails (false).</summary>
    public event Action<bool>? IsDownloadingChanged;

    /// <summary>Fires with 0-100 progress during download.</summary>
    public event Action<int>? DownloadProgressChanged;

    /// <summary>
    /// Fires when the installer has been fully downloaded and is ready to launch.
    /// True = ready; false = no longer ready (e.g. file deleted / new check started).
    /// </summary>
    public event Action<bool>? IsReadyToInstallChanged;

    /// <summary>Fires just before the silent installer is launched — caller should Shutdown().</summary>
    public event Action? ShutdownRequested;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly SettingsService  _settings;
    private CancellationTokenSource? _cts;

    // Update info found from GitHub
    private UpdateInfo? _pendingInstall;

    // Download phase
    private string? _downloadedPath;     // non-null = installer on disk, ready to launch
    private bool    _isDownloading;      // guard: only one download at a time

    // Install phase — CPU idle gating
    private int      _idleSamples;
    private int      _cpuSpikeResets;
    private DateTime _backoffUntil;

    private PerformanceCounter?      _cpuCounter;
    private static readonly LoggerService _log = LoggerService.Instance;

    /// <summary>
    /// Set to true by App when a game session starts; false when it ends.
    /// While true the installer will not launch (download can still proceed).
    /// </summary>
    public volatile bool IsGameModeActive;

    /// <summary>True while the installer .exe is being downloaded.</summary>
    public bool IsDownloading => _isDownloading;

    /// <summary>True when the installer .exe is on disk and ready to launch.</summary>
    public bool IsReadyToInstall => _downloadedPath != null;

    // ── HTTP client ───────────────────────────────────────────────────────────

    private static readonly HttpClient Http;

    static UpdateService()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Systema-Updater/{GetCurrentVersionString()}");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
    }

    // ── Data type ─────────────────────────────────────────────────────────────

    public record UpdateInfo(
        Version        Version,
        string         TagName,
        string         Title,
        string         ReleaseNotes,
        string         DownloadUrl,
        bool           IsPreRelease,
        DateTimeOffset PublishedAt);

    // ── Auto-update lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Starts the background auto-update loop. Call once after app startup.
    /// </summary>
    public void StartAutoUpdate()
    {
        _cts = new CancellationTokenSource();

        // Prime the CPU performance counter — the very first NextValue() always
        // returns 0, so we call it once here and discard the result.
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
        }
        catch { _cpuCounter = null; }

        _ = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>Stops the background loop and releases the CPU counter.</summary>
    public void StopAutoUpdate()
    {
        _cts?.Cancel();
        _cpuCounter?.Dispose();
        _cpuCounter = null;
    }

    /// <summary>
    /// Triggers an immediate check right now, bypassing the 2-hour schedule.
    /// Used by the manual "Check for Updates" button in Settings.
    /// </summary>
    public async Task CheckNowAsync()
    {
        _idleSamples    = 0;
        _cpuSpikeResets = 0;
        _backoffUntil   = DateTime.MinValue;
        // Clear any previously downloaded file so we re-evaluate cleanly
        ClearDownload();
        await DoCheckAsync();
    }

    /// <summary>
    /// Bypasses the CPU idle gate and installs the already-downloaded update immediately.
    /// Used by the manual "Install Now" button in Settings.
    /// </summary>
    public async Task InstallNowAsync()
    {
        if (_downloadedPath == null) return;
        await LaunchInstallerAndShutdownAsync(_downloadedPath, CancellationToken.None);
    }

    // ── Background loop ───────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Wait 20 s after startup before the first check so launch isn't slowed
        if (!await DelayAsync(TimeSpan.FromSeconds(20), ct)) return;

        if (_settings.AutoUpdateEnabled)
            await DoCheckAsync();

        while (!ct.IsCancellationRequested)
        {
            // Tick every 30 s — drives both the 2-hour check schedule and CPU gating
            if (!await DelayAsync(TimeSpan.FromSeconds(30), ct)) return;

            if (!_settings.AutoUpdateEnabled)
            {
                // Auto-update disabled — drop everything and stay quiet.
                // Manual "Check Now" / "Install Now" still work.
                if (_pendingInstall != null)
                {
                    ClearDownload();
                    _pendingInstall = null;
                    OnUpdateAvailable(false);
                    OnStatus("Auto-update is disabled.");
                }
                continue;
            }

            // 2-hour periodic re-check (only when nothing pending)
            if (_pendingInstall == null && _downloadedPath == null && ShouldCheckNow())
                await DoCheckAsync();

            // If we have an update but haven't started downloading, kick it off
            if (_pendingInstall != null && _downloadedPath == null && !_isDownloading)
                _ = BeginDownloadAsync(_pendingInstall);

            // CPU-gated install — only once the file is on disk
            if (_downloadedPath != null)
                await CheckCpuAndMaybeInstallAsync(ct);
        }
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    private async Task DoCheckAsync()
    {
        try
        {
            OnStatus("Checking for updates...");
            var info = await CheckForUpdateAsync();
            SaveLastCheckTime();

            if (info == null)
            {
                _pendingInstall = null;
                ClearDownload();
                OnUpdateAvailable(false);
                OnStatus($"You're up to date  (v{GetCurrentVersionString()})");
                _log.Info("AutoUpdate", "No update available");
            }
            else
            {
                _pendingInstall = info;
                _idleSamples    = 0;
                OnUpdateAvailable(true);
                OnStatus($"Update v{info.Version.ToString(3)} found — downloading...");
                _log.Info("AutoUpdate", $"Update found: {info.TagName}");

                // Kick off download immediately — don't wait for CPU idle
                _ = BeginDownloadAsync(info);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("AutoUpdate", $"Update check failed: {ex.Message}");
            OnStatus("Update check failed — will retry next time.");
        }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async Task BeginDownloadAsync(UpdateInfo info)
    {
        // Guard: only one download at a time; skip if already downloaded
        if (_isDownloading || _downloadedPath != null) return;

        _isDownloading = true;
        IsDownloadingChanged?.Invoke(true);
        DownloadProgressChanged?.Invoke(0);

        _log.Info("AutoUpdate", $"Starting download of {info.TagName}");

        try
        {
            var progress = new Progress<int>(p =>
            {
                DownloadProgressChanged?.Invoke(p);
                OnStatus($"Downloading v{info.Version.ToString(3)}... {p}%");
            });

            var path = await DownloadInstallerAsync(info, progress);

            _downloadedPath = path;
            IsReadyToInstallChanged?.Invoke(true);
            OnStatus(
                $"v{info.Version.ToString(3)} downloaded — " +
                $"will install automatically when CPU is idle");
            _log.Info("AutoUpdate", $"Download complete: {path}");
        }
        catch (Exception ex)
        {
            _log.Error("AutoUpdate", $"Download failed: {ex.Message}", ex);
            OnStatus($"Download failed — will retry. ({ex.Message})");
            // _downloadedPath stays null → loop will retry
        }
        finally
        {
            _isDownloading = false;
            IsDownloadingChanged?.Invoke(false);
        }
    }

    private void ClearDownload()
    {
        if (_downloadedPath != null)
        {
            _downloadedPath = null;
            IsReadyToInstallChanged?.Invoke(false);
        }
    }

    // ── Install (CPU-gated) ───────────────────────────────────────────────────

    private async Task CheckCpuAndMaybeInstallAsync(CancellationToken ct)
    {
        if (_downloadedPath == null) return;

        try
        {
            // ── Game mode guard ───────────────────────────────────────────────
            if (IsGameModeActive)
            {
                _idleSamples = 0; // reset; start fresh after game ends
                OnStatus(
                    $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                    $"paused while game is running");
                return;
            }

            // ── Spike backoff ─────────────────────────────────────────────────
            if (_backoffUntil > DateTime.Now)
            {
                var remaining = _backoffUntil - DateTime.Now;
                OnStatus(
                    $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                    $"CPU was busy, retrying in {(int)remaining.TotalMinutes}m {remaining.Seconds}s");
                return;
            }

            // Backoff just expired — reset and try fresh
            if (_cpuSpikeResets > 0 && _backoffUntil != DateTime.MinValue)
            {
                _cpuSpikeResets = 0;
                _idleSamples    = 0;
                _backoffUntil   = DateTime.MinValue;
                _log.Info("AutoUpdate", "Spike backoff expired — resuming CPU idle check");
            }

            // ── Normal CPU gate ───────────────────────────────────────────────
            float cpu = SampleCpu();

            if (cpu < CpuIdleThreshold)
            {
                _idleSamples++;
                int secondsRemaining = (IdleSamplesRequired - _idleSamples) * 30;

                if (_idleSamples < IdleSamplesRequired)
                {
                    OnStatus(
                        $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                        $"CPU idle ({cpu:F0}%), installing in ~{secondsRemaining / 60}m {secondsRemaining % 60}s");
                }
                else
                {
                    // 5 consecutive minutes of idle — go!
                    await LaunchInstallerAndShutdownAsync(_downloadedPath, ct);
                }
            }
            else
            {
                // CPU spiked
                if (_idleSamples > 0)
                {
                    _idleSamples = 0;
                    _cpuSpikeResets++;

                    _log.Info("AutoUpdate",
                        $"CPU spike ({cpu:F0}%) reset idle counter " +
                        $"(reset #{_cpuSpikeResets}/{MaxSpikeResetsBeforeBackoff})");

                    if (_cpuSpikeResets >= MaxSpikeResetsBeforeBackoff)
                    {
                        _backoffUntil   = DateTime.Now.AddMinutes(SpikeBackoffMinutes);
                        _cpuSpikeResets = 0;
                        _log.Info("AutoUpdate",
                            $"CPU kept spiking — backing off for {SpikeBackoffMinutes} minutes");
                        OnStatus(
                            $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                            $"CPU was busy, retrying in {SpikeBackoffMinutes}m 0s");
                    }
                    else
                    {
                        OnStatus(
                            $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                            $"waiting for CPU < {CpuIdleThreshold:F0}% (currently {cpu:F0}%)");
                    }
                }
                else
                {
                    OnStatus(
                        $"v{_pendingInstall?.Version.ToString(3) ?? "?"} ready to install — " +
                        $"waiting for CPU < {CpuIdleThreshold:F0}% (currently {cpu:F0}%)");
                }
            }
        }
        catch { /* never crash the loop */ }
    }

    private async Task LaunchInstallerAndShutdownAsync(string path, CancellationToken ct)
    {
        try
        {
            _log.Info("AutoUpdate", $"Launching silent installer: {path}");
            OnStatus("Installing update silently — Systema will restart shortly...");

            LaunchSilentInstaller(path);

            // Small pause so the status text is visible before shutdown
            await Task.Delay(1500, CancellationToken.None);

            ShutdownRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("AutoUpdate", $"Install launch failed: {ex.Message}", ex);
            // Clear the bad path so the loop re-downloads next time
            ClearDownload();
            _idleSamples = 0;
            OnStatus($"Install failed — will re-download and retry. ({ex.Message})");
        }
    }

    // ── Core update check ─────────────────────────────────────────────────────

    /// <summary>
    /// Queries GitHub for the newest release newer than the current version.
    /// Returns <c>null</c> if up-to-date or if the check fails.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var current  = GetCurrentVersion();
            var json     = await Http.GetStringAsync(ApiUrl);
            var releases = JsonNode.Parse(json)?.AsArray();
            if (releases == null) return null;

            UpdateInfo? best = null;

            foreach (var r in releases)
            {
                if (r == null) continue;

                bool isPreRelease = r["prerelease"]?.GetValue<bool>() ?? false;
                bool isDraft      = r["draft"]?.GetValue<bool>()      ?? false;

                if (isDraft) continue;
                if (isPreRelease && !IsPreReleaseBuild) continue;

                var tagName = r["tag_name"]?.GetValue<string>() ?? string.Empty;
                if (!TryParseVersion(tagName, out var ver)) continue;
                if (ver <= current) continue;

                // Find the installer .exe asset
                string dlUrl = string.Empty;
                var assets = r["assets"]?.AsArray();
                if (assets != null)
                {
                    foreach (var asset in assets)
                    {
                        var name = asset?["name"]?.GetValue<string>() ?? string.Empty;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            dlUrl = asset?["browser_download_url"]?.GetValue<string>()
                                    ?? string.Empty;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(dlUrl)) continue;

                if (best == null || ver > best.Version)
                {
                    best = new UpdateInfo(
                        Version:      ver,
                        TagName:      tagName,
                        Title:        r["name"]?.GetValue<string>() ?? tagName,
                        ReleaseNotes: r["body"]?.GetValue<string>() ?? string.Empty,
                        DownloadUrl:  dlUrl,
                        IsPreRelease: isPreRelease,
                        PublishedAt:  DateTimeOffset.TryParse(
                            r["published_at"]?.GetValue<string>(), out var dt)
                            ? dt : DateTimeOffset.Now);
                }
            }

            return best;
        }
        catch { return null; }
    }

    // ── Download & silent install ─────────────────────────────────────────────

    public async Task<string> DownloadInstallerAsync(
        UpdateInfo info,
        IProgress<int>? progress = null)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"Systema_Setup_{info.TagName.TrimStart('v')}.exe");

        using var response = await Http.GetAsync(
            info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        long read  = 0;
        var  buf   = new byte[81_920]; // 80 KB chunks

        await using var src  = await response.Content.ReadAsStreamAsync();
        await using var dest = File.Create(tempPath);

        int bytes;
        while ((bytes = await src.ReadAsync(buf)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, bytes));
            read += bytes;
            if (total > 0)
                progress?.Report((int)(read * 100L / total));
        }

        return tempPath;
    }

    /// <summary>
    /// Launches the installer completely silently — no UI, no prompts, no restart.
    /// Inno Setup flags: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-
    /// </summary>
    public void LaunchSilentInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName        = installerPath,
            Arguments       = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            UseShellExecute = true,
        });
    }

    // ── CPU sampling ──────────────────────────────────────────────────────────

    private float SampleCpu()
    {
        try { return _cpuCounter?.NextValue() ?? 0f; }
        catch { return 0f; }
    }

    // ── Schedule helpers ──────────────────────────────────────────────────────

    private static bool ShouldCheckNow()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"SOFTWARE\Systema");
            var raw = key?.GetValue("LastUpdateCheck") as string;
            if (raw != null && DateTime.TryParse(raw, out var last))
                return (DateTime.Now - last).TotalHours >= CheckIntervalHours;
            return true; // never checked
        }
        catch { return true; }
    }

    private static void SaveLastCheckTime()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .CreateSubKey(@"SOFTWARE\Systema", writable: true);
            key?.SetValue("LastUpdateCheck", DateTime.Now.ToString("O"));
        }
        catch { }
    }

    // ── Version helpers ───────────────────────────────────────────────────────

    public static Version GetCurrentVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v != null ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);
        }
        catch { return new Version(0, 0, 0); }
    }

    public static string GetCurrentVersionString()
    {
        var v = GetCurrentVersion();
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private static bool TryParseVersion(string tag, out Version version)
    {
        var s    = tag.TrimStart('v');
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out version!);
    }

    // ── Event helpers ─────────────────────────────────────────────────────────

    private void OnStatus(string msg)          => StatusChanged?.Invoke(msg);
    private void OnUpdateAvailable(bool value) => UpdateAvailableChanged?.Invoke(value);

    // ── Misc ──────────────────────────────────────────────────────────────────

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try   { await Task.Delay(delay, ct); return true; }
        catch { return false; }
    }

    public void Dispose() => StopAutoUpdate();
}
