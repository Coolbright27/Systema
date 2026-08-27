// ════════════════════════════════════════════════════════════════════════════
// TaskSleepService.SkipRules.cs  ·  Which processes the nap engine leaves alone
// ════════════════════════════════════════════════════════════════════════════
//
// ShouldSkip and the predicates it calls. Split out of TaskSleepService.cs so
// the "never touch this" rules can be read as one set rather than hunted for in
// a 5,700-line file.
//
// ORDER IS LOAD-BEARING. The checks run top to bottom and the FIRST match wins,
// which is how a process ends up with one skip reason rather than several. The
// permanent safety layers (system whitelist, Windows binaries, System integrity,
// service accounts, security software) deliberately run before anything the user
// can configure, so no setting can switch them off.
//
// Every branch sets a SkipReason before returning. That string is what the
// monitor UI shows and what the CPU-CAP diagnostic reports, so a branch that
// returns true without setting one makes a process look skipped for no reason.
// ════════════════════════════════════════════════════════════════════════════

using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Systema.Models;

namespace Systema.Services;

public sealed partial class TaskSleepService
{
    // ── Process filtering ──────────────────────────────────────────────────────
    // ── Process filtering ──────────────────────────────────────────────────────

    /// <summary>Why a rule exists, which is what decides whether a setting may bypass it.</summary>
    internal enum SkipTag
    {
        /// <summary>Systema itself. Napping the napper deadlocks the monitor thread.</summary>
        Self,
        /// <summary>Non-negotiable safety. No setting may switch these off.</summary>
        Permanent,
        /// <summary>True only for a while: a backoff window, or an active launch boost.</summary>
        Transient,
        /// <summary>The user asked for this, via a toggle or the never-nap list.</summary>
        UserSetting,
        /// <summary>The app is visibly or audibly in use right now.</summary>
        Activity,
    }

    /// <summary>Everything a rule needs, so rules stay one-liners instead of taking five parameters.</summary>
    private readonly record struct SkipContext(
        Process Proc,
        HashSet<int> ProtectedPids,
        TaskSleepSettings Settings,
        Dictionary<string, TaskSleepAppRule> Rules,
        HashSet<int>? AudioPids);

    private sealed record SkipRule(string Reason, SkipTag Tag, Func<SkipContext, bool> Applies);

    private SkipRule[]? _skipRules;

    /// <summary>
    /// The skip rules, in evaluation order. FIRST MATCH WINS, which is what gives a process one
    /// skip reason rather than several, so ORDER IS LOAD-BEARING.
    ///
    /// Permanent rules deliberately precede every UserSetting rule: a user toggle must never be
    /// able to un-protect a system process, a Windows binary, a service account or an AV process.
    /// Adding a rule means inserting it at the right point in this list rather than appending.
    ///
    /// Built once and cached: this runs for every process on every tick.
    /// </summary>
    private SkipRule[] SkipRules => _skipRules ??= new SkipRule[]
    {
        new("Systema itself", SkipTag.Self, c => c.Proc.Id == OwnPid),
        new("System PID",     SkipTag.Permanent, c => c.Proc.Id <= 4),

        // Repeated OpenProcess failures mean we will not win; stop burning handles retrying.
        new("Access denied", SkipTag.Transient, IsAccessDeniedBackoff),

        // ── PERMANENT SAFETY LAYERS — never bypassed, regardless of user settings ──

        // Explicit whitelist of processes that must never be throttled.
        new("System process (whitelist)", SkipTag.Permanent, c => IsSystemProcess(c.Proc)),

        // Any executable under %windir%\System32 or SysWOW64. Catches Microsoft OS helpers that
        // run in the USER session (so the service-account and elevated checks miss them) and are
        // not in the static whitelist, e.g. wpcmon.exe. You cannot drop an arbitrary exe into
        // System32 without admin, so a path match is a reliable "this is a Windows binary".
        new("Windows system component", SkipTag.Permanent, c => IsWindowsSystemBinary(c.Proc.Id)),

        // Admin-only processes; throttling them can corrupt system state.
        new("Elevated/System integrity (non-bypassable)", SkipTag.Permanent,
            c => IsElevatedOrSystemProcess(c.Proc.Id)),

        // App Nap targets user applications only.
        new("Service account (SYSTEM/LocalService/NetworkService)", SkipTag.Permanent,
            c => IsServiceAccount(c.Proc.Id)),

        // Never nap a process while its launch boost is active. The two would ping-pong every
        // tick, and worse, the nap path captures the CURRENT priority as the value to restore
        // later: if that is the boosted High, the process is restored to High permanently.
        // The boost expires in <= 120 s and restores the true original, so leave it to that.
        new("Launch Boost active", SkipTag.Transient, c => IsLaunchBoosted(c.Proc.Id)),

        new("Security/AV critical", SkipTag.Permanent, c => IsSecurityCritical(c.Proc.ProcessName)),

        // Earned its place by re-raising its own priority six times in 90 s. See the
        // auto-whitelist logic in the engine.
        new("Auto-whitelisted", SkipTag.Permanent, c => _napSuppressed.Contains(c.Proc.ProcessName)),

        // ── User-configurable ──
        new("Windows service", SkipTag.UserSetting,
            c => c.Settings.ExcludeSystemServices && IsSystemService(c.Proc)),
        new("Foreground", SkipTag.Activity,
            c => c.Settings.IgnoreForeground && c.ProtectedPids.Contains(c.Proc.Id)),
        new("Never-nap list", SkipTag.UserSetting,
            c => c.Rules.TryGetValue(c.Proc.ProcessName, out var r) && r.IsBlacklisted),

        // If a process is playing audio, using the mic, or is a known always-active app, it must
        // never be napped by ANY path. This is the App Nap approach: strict, but aware of use.
        new("Audio/media active", SkipTag.Activity,
            c => c.AudioPids != null && IsAudioProtected(c.Proc.Id, c.Proc.ProcessName, c.AudioPids)),
    };

    /// <summary>Its own method because it logs once on the third failure, which a lambda cannot.</summary>
    private bool IsAccessDeniedBackoff(SkipContext c)
    {
        if (!TryState(c.Proc.Id, out var st) || st.AccessDenied is not { } denied) return false;
        if (denied.Count < 3 || (DateTime.UtcNow - denied.LastFail).TotalSeconds >= 60) return false;

        if (denied.Count == 3)
            _log.Info("TaskSleepService",
                      $"Access-denied backoff: skipping '{c.Proc.ProcessName}' (PID {c.Proc.Id}) — denied {denied.Count}x in 60s");
        return true;
    }

    private bool ShouldSkip(
        Process proc, HashSet<int> protectedPids, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules,
        HashSet<int>? audioPids = null)
    {
        var ctx = new SkipContext(proc, protectedPids, s, rules, audioPids);

        foreach (var rule in SkipRules)
        {
            if (!rule.Applies(ctx)) continue;
            // Every skip sets a reason: it is what the monitor UI shows and what the CPU-CAP
            // diagnostic reports. Returning true without one makes a process look skipped for
            // no reason, which is how skip bugs used to hide.
            StateFor(proc.Id).SkipReason = rule.Reason;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the process name matches a known security/AV process that must
    /// never be throttled. Checks both the static hardcoded list and any AV detected
    /// at runtime via Windows Security Center.
    /// </summary>
    private bool IsSecurityCritical(string processName) =>
        SecurityCriticalProcessNames.Contains(processName) ||
        _detectedAvProcessNames.Contains(processName);

    private bool IsSystemProcess(Process proc)
    {
        // Name-based exclusion only. We no longer use SessionId == 0 as a blanket guard,
        // because that would protect every background service process — including Windows
        // Update workers, cloud-sync agents, telemetry runners, etc. — which are exactly
        // what we want to throttle. SystemProcessNames now explicitly enumerates everything
        // that must never be touched; everything else (including non-critical session 0
        // processes) is eligible for throttling.
        //
        // Also check runtime-detected critical services to catch new services added in OS updates.
        return SystemProcessNames.Contains(proc.ProcessName) ||
               _detectedCriticalServices.Contains(proc.ProcessName);
    }

    /// <summary>
    /// Returns true if the process runs at High or System integrity level (elevated/admin).
    /// Results are cached per-PID since integrity level never changes during a process's lifetime.
    /// Uses <c>GetTokenInformation(TokenIntegrityLevel)</c> to read the mandatory label SID.
    /// </summary>
    private bool IsElevatedOrSystemProcess(int pid)
    {
        // Check cache first — integrity level is immutable for a process
        var elSt = StateFor(pid);
        if (elSt.ElevatedCache is { } cached)
            return cached;

        bool elevated = false;
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
        {
            // Can't open → assume not elevated (system-critical processes are already
            // in SystemProcessNames and would be skipped before reaching this check)
            elSt.ElevatedCache = false;
            return false;
        }

        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken))
            {
                elSt.ElevatedCache = false;
                return false;
            }

            try
            {
                // Query the size needed for the integrity level info
                GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
                if (needed <= 0)
                {
                    elSt.ElevatedCache = false;
                    return false;
                }

                IntPtr buf = Marshal.AllocHGlobal(needed);
                try
                {
                    if (GetTokenInformation(hToken, TokenIntegrityLevel, buf, needed, out _))
                    {
                        // TOKEN_MANDATORY_LABEL struct: first field is SID_AND_ATTRIBUTES,
                        // which starts with a pointer to the SID
                        IntPtr pSid = Marshal.ReadIntPtr(buf);
                        if (pSid != IntPtr.Zero)
                        {
                            // Get the last sub-authority (the RID) which is the integrity level
                            IntPtr countPtr = GetSidSubAuthorityCount(pSid);
                            if (countPtr != IntPtr.Zero)
                            {
                                byte count = Marshal.ReadByte(countPtr);
                                if (count > 0)
                                {
                                    IntPtr ridPtr = GetSidSubAuthority(pSid, (uint)(count - 1));
                                    if (ridPtr != IntPtr.Zero)
                                    {
                                        int rid = Marshal.ReadInt32(ridPtr);
                                        elevated = rid >= SECURITY_MANDATORY_HIGH_RID;
                                    }
                                }
                            }
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(hToken); }
        }
        finally { CloseHandle(hProcess); }

        elSt.ElevatedCache = elevated;
        return elevated;
    }

    /// <summary>
    /// Returns true when the process runs as one of the well-known service accounts —
    /// NT AUTHORITY\SYSTEM (S-1-5-18), LOCAL SERVICE (S-1-5-19), or NETWORK SERVICE
    /// (S-1-5-20). App Nap targets user applications only, so these are permanently
    /// excluded (App Nap exclusion rule 4). Reads the token user SID once and caches it.
    /// </summary>
    private bool IsServiceAccount(int pid)
    {
        var saSt = StateFor(pid);
        if (saSt.ServiceAccountCache is { } cached) return cached;

        bool isService = false;
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero) { saSt.ServiceAccountCache = false; return false; }
        try
        {
            if (OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hToken))
            {
                try
                {
                    GetTokenInformation(hToken, TokenUser, IntPtr.Zero, 0, out int needed);
                    if (needed > 0)
                    {
                        IntPtr buf = Marshal.AllocHGlobal(needed);
                        try
                        {
                            if (GetTokenInformation(hToken, TokenUser, buf, needed, out _))
                            {
                                // TOKEN_USER { SID_AND_ATTRIBUTES User; } — first field is a pointer to the SID.
                                IntPtr pSid = Marshal.ReadIntPtr(buf);
                                if (pSid != IntPtr.Zero && ConvertSidToStringSid(pSid, out IntPtr strSid))
                                {
                                    string? sid = Marshal.PtrToStringUni(strSid);
                                    LocalFree(strSid);
                                    isService = sid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";
                                }
                            }
                        }
                        finally { Marshal.FreeHGlobal(buf); }
                    }
                }
                finally { CloseHandle(hToken); }
            }
        }
        catch { /* best-effort — default to "not a service account" */ }
        finally { CloseHandle(hProcess); }

        saSt.ServiceAccountCache = isService;
        return isService;
    }

    // %windir%\System32\ and \SysWOW64\ with a trailing separator, resolved once. Any process
    // whose image path starts with one of these is a Windows OS component (you can't place an
    // exe there without admin), so it's never napped — even when it runs in the user session
    // (e.g. wpcmon.exe) and so escapes the service-account / elevated / name-whitelist checks.
    private static readonly string _system32Dir =
        Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd('\\') + "\\";
    private static readonly string _sysWow64Dir =
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86).TrimEnd('\\') + "\\";

    /// <summary>True if the process's executable lives under System32 / SysWOW64 — a Windows
    /// system component that must never be napped. Cached per-PID (the image path is immutable).</summary>
    private bool IsWindowsSystemBinary(int pid)
    {
        var st = StateFor(pid);
        if (st.IsWindowsSystemBinary is { } cached) return cached;

        bool result = false;
        string? path = GetProcessImagePath(pid);
        if (!string.IsNullOrEmpty(path))
            result = path.StartsWith(_system32Dir, StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith(_sysWow64Dir, StringComparison.OrdinalIgnoreCase);

        st.IsWindowsSystemBinary = result;
        return result;
    }

    /// <summary>Full executable path for a PID via QueryFullProcessImageName, or null if it
    /// can't be read (access denied / exited). Systema runs elevated, so this succeeds for
    /// virtually all user-session processes.</summary>
    private static string? GetProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int cap = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Returns true if the process is running as a Windows service (has a service owner).
    /// Services should NEVER be throttled unless explicitly in the SystemProcessNames whitelist,
    /// because throttling a service mid-operation can leave it in a corrupted or inconsistent state
    /// (e.g., Windows Update COM registration failures).
    /// This is a best-effort check using WMI — if WMI is unavailable or the check fails, returns false.
    /// </summary>
    private bool IsSystemService(Process proc)
    {
        try
        {
            // Query WMI for services matching this process ID
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Service WHERE ProcessId = {proc.Id}");
            foreach (ManagementObject service in searcher.Get())
            {
                // If any service uses this PID, it's a service process
                var name = service["Name"] as string;
                if (!string.IsNullOrEmpty(name))
                {
                    _log.Info("TaskSleepService",
                        $"Detected service '{name}' (PID {proc.Id}) — protecting from throttling");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            // WMI failures are non-fatal — just log and return false.
            // This prevents a WMI issue from breaking the monitor thread.
            _log.Warn("TaskSleepService", $"IsSystemService WMI check failed for PID {proc.Id}: {ex.Message}");
        }
        return false;
    }
}
