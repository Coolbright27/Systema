using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace Systema.Services;

/// <summary>
/// Lets the single-file <c>Systema.exe</c> install itself — no separate installer
/// binary required.
/// <para>
/// This is the Smart-App-Control-safe install path. SAC blocks the Inno Setup
/// installer because Inno extracts an unsigned engine (<c>.tmp</c>) to %TEMP% and
/// executes it (the "Error 4551: Application Control policy has blocked this file"
/// you get on a SAC-enforced PC). It does NOT block <c>Systema.exe</c> itself — the
/// single-file bundle loads its managed code from memory and only the
/// Microsoft-signed native runtime ever touches disk, so SAC lets the app run. By
/// performing the install from <c>Systema.exe</c> directly, the only executable
/// involved is the one SAC already trusts.
/// </para>
/// <para>
/// This is ADDITIVE. The Inno installer and the auto-updater are unchanged, so
/// existing users keep updating exactly as before. The canonical install location
/// is <c>%ProgramFiles%\Systema</c> — the same path Inno uses — so an
/// already-installed (Inno or self-installed) copy is detected via
/// <see cref="IsRunningInstalled"/> and this code stays dormant for it.
/// </para>
/// </summary>
public static class SelfInstallService
{
    private static readonly LoggerService Log = LoggerService.Instance;

    public const string AppName = "Systema";

    /// <summary><c>C:\Program Files\Systema</c> (matches the Inno installer's {autopf}\Systema).</summary>
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    public static string InstalledExe => Path.Combine(InstallDir, "Systema.exe");

    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Systema";

    /// <summary>True when the running exe IS the installed copy (in Program Files\Systema).</summary>
    public static bool IsRunningInstalled()
    {
        try
        {
            var self = Environment.ProcessPath;
            return self != null &&
                   string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>True when Systema is already installed at the canonical location.</summary>
    public static bool IsInstalled()
    {
        try { return File.Exists(InstalledExe); } catch { return false; }
    }

    /// <summary>
    /// Copies the running exe into <see cref="InstallDir"/>, creates Start Menu and
    /// Desktop shortcuts, and registers the Add/Remove Programs uninstall entry.
    /// Returns the installed exe path, or <c>null</c> on failure. Requires admin
    /// (the app already auto-elevates via its manifest).
    /// </summary>
    public static string? Install(bool silent, string version)
    {
        try
        {
            var self = Environment.ProcessPath;
            if (self == null) { Log.Warn("SelfInstall", "Cannot resolve own exe path"); return null; }

            bool wasInstalled = IsInstalled();   // already on disk → this is an update, not a first install

            if (!IsRunningInstalled())
            {
                Directory.CreateDirectory(InstallDir);
                // Free the target exe if a previous copy is running (update-in-place).
                KillOtherInstances();
                // File.Copy intentionally copies only the primary data stream, NOT the
                // Zone.Identifier ADS — so the installed copy has no "from the internet"
                // mark even when the source was downloaded.
                CopyWithRetry(self, InstalledExe);
            }

            CreateShortcut(StartMenuShortcut);
            // Desktop shortcut ONLY on a fresh, interactive install. An auto-update (silent) or a
            // re-install must NOT drop a new icon on the desktop — the Inno installer leaves the
            // desktop icon unchecked by default, so a self-install update appearing as a new
            // desktop "installer" was the reported bug.
            if (!silent && !wasInstalled)
                CreateShortcut(DesktopShortcut);
            WriteUninstallEntry(version);

            Log.Info("SelfInstall", $"Installed to {InstalledExe} (v{version})");
            return InstalledExe;
        }
        catch (Exception ex)
        {
            Log.Error("SelfInstall", "Install failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Restores the Windows settings Systema changed, removes shortcuts and the
    /// Add/Remove Programs entry, then schedules deletion of the install folder
    /// (the running exe can't delete itself, so a tiny detached cmd does it after exit).
    /// </summary>
    public static void Uninstall()
    {
        try { UninstallCleanupService.RunCleanup(); }
        catch (Exception ex) { Log.Warn("SelfInstall", $"cleanup failed: {ex.Message}"); }

        try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); } catch { }
        try { if (File.Exists(DesktopShortcut))   File.Delete(DesktopShortcut); } catch { }

        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Warn("SelfInstall", $"ARP removal failed: {ex.Message}"); }

        try { ScheduleDirDeletion(InstallDir); }
        catch (Exception ex) { Log.Warn("SelfInstall", $"schedule delete failed: {ex.Message}"); }

        Log.Info("SelfInstall", "Uninstalled");
    }

    // ── Shortcut locations ────────────────────────────────────────────────────
    private static string ProgramsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
    private static string StartMenuShortcut => Path.Combine(ProgramsDir, "Systema.lnk");
    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Systema.lnk");

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Creates a .lnk via the WScript.Shell COM object (late-bound — no project reference).</summary>
    private static void CreateShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            object? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            object? sc = shellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return;

            var t = sc.GetType();
            void Set(string prop, object val) =>
                t.InvokeMember(prop, BindingFlags.SetProperty, null, sc, new[] { val });

            Set("TargetPath", InstalledExe);
            Set("WorkingDirectory", InstallDir);
            Set("IconLocation", InstalledExe + ",0");
            Set("Description", "High-performance Windows optimization suite");
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch (Exception ex) { Log.Warn("SelfInstall", $"shortcut '{lnkPath}': {ex.Message}"); }
    }

    private static void WriteUninstallEntry(string version)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(UninstallKey, writable: true);
            if (k == null) return;
            k.SetValue("DisplayName",     "Systema");
            k.SetValue("DisplayVersion",  version);
            k.SetValue("Publisher",       "Systema");
            k.SetValue("DisplayIcon",     InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString",      $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silent");
            k.SetValue("URLInfoAbout",    "https://github.com/Coolbright27/Systema");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
            catch { /* size is cosmetic */ }
        }
        catch (Exception ex) { Log.Warn("SelfInstall", $"ARP entry failed: {ex.Message}"); }
    }

    /// <summary>
    /// Frees the installed Systema.exe so it can be overwritten, by getting any OTHER running
    /// Systema instances to exit. Prefers a graceful exit and never tree-kills:
    /// <list type="bullet">
    /// <item>When invoked as an AUTO-UPDATE, the old instance's own updater shuts it down a
    /// moment after launching us, so we WAIT for that — hard-killing a windowed app instead
    /// leaves a frozen "Not Responding" ghost window behind (the freeze users reported).</item>
    /// <item>Anything still alive after the wait gets a SINGLE <c>Kill()</c>, never
    /// <c>entireProcessTree</c>: the updater may have launched this installer as a CHILD of the
    /// old instance, and a tree-kill would terminate us (and our just-spawned replacement).</item>
    /// </list>
    /// </summary>
    private static void KillOtherInstances()
    {
        try
        {
            int self = Environment.ProcessId;
            var others = Process.GetProcessesByName("Systema")
                                .Where(p => p.Id != self)
                                .ToList();
            if (others.Count == 0) return;

            // Wait up to ~3s for a graceful self-shutdown (auto-update path).
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline && others.Any(p => !HasExitedSafe(p)))
                System.Threading.Thread.Sleep(250);

            // Force-close whatever is still alive — single Kill only.
            foreach (var p in others)
            {
                try { if (!HasExitedSafe(p)) { p.Kill(); p.WaitForExit(2000); } }
                catch (Exception ex) { Log.Warn("SelfInstall", $"kill PID {p.Id}: {ex.Message}"); }
                finally { p.Dispose(); }
            }

            // A killed instance never ran its clean Dispose, so its heartbeat file is still
            // fresh. Clear it so the relaunched copy can't misread a ghost-beat and bail out in
            // a single-instance race.
            HeartbeatService.Clear();
        }
        catch (Exception ex) { Log.Warn("SelfInstall", $"KillOtherInstances: {ex.Message}"); }
    }

    private static bool HasExitedSafe(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    private static void CopyWithRetry(string src, string dst)
    {
        Exception? last = null;
        for (int i = 0; i < 6; i++)
        {
            try { File.Copy(src, dst, overwrite: true); return; }
            catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(400); }
        }
        if (last != null) throw last;
    }

    private static void ScheduleDirDeletion(string dir)
    {
        // Detached cmd: wait ~2 s for this process to exit, then remove the folder.
        // cmd.exe is Microsoft-signed, so this step is allowed under SAC.
        Process.Start(new ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"",
            CreateNoWindow  = true,
            UseShellExecute = false,
            WindowStyle     = ProcessWindowStyle.Hidden,
        });
    }
}
