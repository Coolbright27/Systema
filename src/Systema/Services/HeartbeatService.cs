using System.Diagnostics;
using System.IO;

namespace Systema.Services;

/// <summary>
/// Lightweight "liveness" file the running Systema process touches every
/// <see cref="IntervalSeconds"/> seconds. On a duplicate launch, App.xaml.cs
/// reads the file's LastWriteTime — if it's older than <see cref="StaleSeconds"/>
/// the running instance is treated as hung/zombied and force-killed so the new
/// launch can acquire the single-instance mutex. Without this, a runtime hang
/// (any timer/lock/COM call that wedges the running app) leaves the mutex
/// permanently held and the user has to reinstall to launch again.
///
/// Implementation deliberately uses just a file timestamp (no named kernel
/// objects, no IPC primitives, no threads) so it adds zero "suspicious"
/// surface for Smart App Control / Defender behavioural heuristics.
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    /// <summary>How often the heartbeat file is touched while alive.</summary>
    public  const int IntervalSeconds = 10;
    /// <summary>A heartbeat older than this is treated as "hung" by a new launch.</summary>
    public  const int StaleSeconds    = 30;

    private static readonly LoggerService _log = LoggerService.Instance;

    private System.Threading.Timer? _timer;
    private bool _disposed;

    /// <summary><c>%LOCALAPPDATA%\Systema\heartbeat</c> — per-user.</summary>
    public static string HeartbeatPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Systema");
            return Path.Combine(dir, "heartbeat");
        }
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatPath)!);
            Touch(); // immediate first beat so duplicate launches don't kill us during startup
            _timer = new System.Threading.Timer(
                _ => Touch(), null,
                TimeSpan.FromSeconds(IntervalSeconds),
                TimeSpan.FromSeconds(IntervalSeconds));
            _log.Info("Heartbeat", $"Started — touching {HeartbeatPath} every {IntervalSeconds}s");
        }
        catch (Exception ex)
        {
            // Non-fatal: missing heartbeat just means the auto-recovery can't kick in;
            // the app still runs normally.
            _log.Warn("Heartbeat", $"Start failed (non-fatal): {ex.Message}");
        }
    }

    private static void Touch()
    {
        try
        {
            if (!File.Exists(HeartbeatPath))
                File.WriteAllText(HeartbeatPath, string.Empty);
            File.SetLastWriteTimeUtc(HeartbeatPath, DateTime.UtcNow);
        }
        catch { /* best-effort — disk full, locked, etc. */ }
    }

    /// <summary>Removes the heartbeat so a future launch never sees a stale ghost-beat
    /// after a clean shutdown.</summary>
    public static void Clear()
    {
        try { if (File.Exists(HeartbeatPath)) File.Delete(HeartbeatPath); }
        catch { }
    }

    /// <summary>
    /// True when the heartbeat file is missing, unreadable, or older than the
    /// staleness threshold — i.e. the running Systema is no longer alive.
    /// </summary>
    public static bool IsHeartbeatStale()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return true;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(HeartbeatPath);
            return age.TotalSeconds > StaleSeconds;
        }
        catch { return true; }
    }

    /// <summary>
    /// Force-kills any other Systema.exe processes (skipping the current one) so a
    /// fresh launch can acquire the single-instance mutex. Returns the count
    /// killed. Best-effort and safe to call as admin (Systema requires admin
    /// already, so it has permission to terminate its own user's other instances).
    /// </summary>
    public static int KillHungInstances()
    {
        int killed = 0;
        int self = Process.GetCurrentProcess().Id;
        try
        {
            foreach (var p in Process.GetProcessesByName("Systema"))
            {
                try
                {
                    if (p.Id == self) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    killed++;
                }
                catch (Exception ex)
                {
                    _log.Warn("Heartbeat", $"Could not kill PID {p.Id}: {ex.Message}");
                }
                finally { p.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Heartbeat", $"KillHungInstances enumeration failed: {ex.Message}");
        }
        return killed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer?.Dispose(); } catch { }
        Clear(); // clean exit → no stale heartbeat for the next launch to misread
    }
}
