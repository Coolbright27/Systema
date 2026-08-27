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

    private bool ShouldSkip(
        Process proc, HashSet<int> protectedPids, TaskSleepSettings s,
        Dictionary<string, TaskSleepAppRule> rules,
        HashSet<int>? audioPids = null)
    {
        if (proc.Id == OwnPid) { StateFor(proc.Id).SkipReason = "Systema itself"; return true; }
        if (proc.Id <= 4) { StateFor(proc.Id).SkipReason ="System PID"; return true; }
        if (TryState(proc.Id, out var adSt) && adSt.AccessDenied is { } denied &&
            denied.Count >= 3 &&
            (DateTime.UtcNow - denied.LastFail).TotalSeconds < 60)
        {
            if (denied.Count == 3)
                _log.Info("TaskSleepService", $"Access-denied backoff: skipping '{proc.ProcessName}' (PID {proc.Id}) — denied {denied.Count}× in 60s");
            StateFor(proc.Id).SkipReason ="Access denied";
            return true;
        }

        // ── PERMANENT SAFETY LAYERS — Never bypassed, regardless of user settings ──
        // These are non-negotiable to prevent corruption of critical OS functionality.

        // 1. System process names — explicit whitelist of processes that must never be throttled.
        //    This takes priority over ALL user settings.
        if (IsSystemProcess(proc)) { StateFor(proc.Id).SkipReason ="System process (whitelist)"; return true; }

        // 1b. Windows system components — any executable under %windir%\System32 or SysWOW64.
        //     Catches Microsoft OS helpers that run in the USER session (so the service-account
        //     and elevated checks miss them) and whose name isn't in the static whitelist —
        //     e.g. wpcmon.exe (Family Safety / Parental Controls monitor). The image path is
        //     immutable, so the result is cached per-PID. You can't drop an arbitrary exe into
        //     System32 without admin, so a path match is a reliable "this is a Windows binary".
        if (IsWindowsSystemBinary(proc.Id)) { StateFor(proc.Id).SkipReason = "Windows system component"; return true; }

        // 2. Elevated/System integrity — ALWAYS skip, no toggle. These are admin-only
        //    processes and throttling them can corrupt system state.
        if (IsElevatedOrSystemProcess(proc.Id)) { StateFor(proc.Id).SkipReason ="Elevated/System integrity (non-bypassable)"; return true; }

        // 2b. Service accounts (SYSTEM / LOCAL SERVICE / NETWORK SERVICE) — App Nap
        //     targets user applications only; these are permanently excluded (rule 4).
        if (IsServiceAccount(proc.Id)) { StateFor(proc.Id).SkipReason ="Service account (SYSTEM/LocalService/NetworkService)"; return true; }

        // 2c. Launch-Boosted — never nap a process while its launch boost is active. Launch
        //     Boost (High priority / I-O / EcoQoS-off, re-asserted every 1.5 s) and a nap
        //     (Idle / EcoQoS / CPU cap / GPU idle, every ~2 s) would otherwise ping-pong
        //     every tick. Worse, the nap path captures the CURRENT priority as the value to
        //     restore later — if that's the boosted HIGH, the process is restored to High
        //     permanently. Leaving it to the boost (which expires in ≤120 s and restores the
        //     true original) avoids both. It becomes nap-eligible the instant the boost ends.
        if (IsLaunchBoosted(proc.Id)) { StateFor(proc.Id).SkipReason ="Launch Boost active"; return true; }

        // 3. Security-critical (AV, Defender, etc.) — ALWAYS skip, non-negotiable
        if (IsSecurityCritical(proc.ProcessName)) { StateFor(proc.Id).SkipReason ="Security/AV critical"; return true; }

        // 4. Auto-whitelisted processes (previously caused issues) — ALWAYS skip
        if (_napSuppressed.Contains(proc.ProcessName)) { StateFor(proc.Id).SkipReason ="Auto-whitelisted"; return true; }

        // ── User-configurable checks (toggleable via settings) ──
        if (s.ExcludeSystemServices && IsSystemService(proc)) { StateFor(proc.Id).SkipReason ="Windows service"; return true; }
        if (s.IgnoreForeground && protectedPids.Contains(proc.Id)) { StateFor(proc.Id).SkipReason ="Foreground"; return true; }
        if (rules.TryGetValue(proc.ProcessName, out var rule) && rule.IsBlacklisted) { StateFor(proc.Id).SkipReason ="Never-nap list"; return true; }

        // Global audio protection gate: if any process is actively playing audio,
        // using the microphone, or is a known always-active app (media player, OBS),
        // it must NEVER be napped by any path (CPU, smart, aggressive, idle, background).
        // This is the iOS/macOS App Nap approach: strict throttling but smart about what's in use.
        if (audioPids != null && IsAudioProtected(proc.Id, proc.ProcessName, audioPids))
        {
            StateFor(proc.Id).SkipReason ="Audio/media active";
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
