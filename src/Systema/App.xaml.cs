// ════════════════════════════════════════════════════════════════════════════
// App.xaml.cs  ·  Application entry point and manual DI composition root
// ════════════════════════════════════════════════════════════════════════════
//
// No IoC container — all services are new'd here in OnStartup() and injected
// into ViewModels by constructor parameter.
//
// TAB / SECTION MAP  (ViewModel ↔ Service(s) it receives)
//   DashboardViewModel    ← HealthScoreService, PowerPlanService
//   MemoryViewModel       ← MemoryService, StartupService
//   ServicesViewModel     ← ServiceControlService, OptionalFeaturesService, RestorePointService, SettingsService
//   VisualViewModel       ← AnimationService, PowerPlanService
//   GameBoosterViewModel  ← GameBoosterService (→ ServiceControlService, SettingsService, ProcessLassoService)
//   SettingsViewModel     ← SettingsService
//   ToolsViewModel        ← RealtekCleanerService, CoreParkingService, RestorePointService,
//                           SettingsService, DnsService, WindowsUpdateTweaksService,
//                           SystemStabilityService
//   TaskSleepViewModel    ← (self-contained; creates TaskSleepService internally)
//   NetworkViewModel      ← DnsService, DefenderService  [wired inside MainViewModel if present]
//
// ADD A NEW TAB
//   1. Create src/Systema/Views/XxxView.xaml + XxxView.xaml.cs
//   2. Create src/Systema/ViewModels/XxxViewModel.cs  (implement IAutoRefreshable if it needs periodic refresh)
//   3. Instantiate service(s) + new XxxViewModel(service) in the composition block in OnStartup() below
//   4. Add XxxViewModel property to MainViewModel.cs and pass it in the constructor call here
//   5. Add nav button + section binding in Views/MainWindow.xaml
//
// ADD A NEW SERVICE  (no new tab needed)
//   1. Instantiate in the composition block below
//   2. Pass it to the appropriate ViewModel constructor
//
// RELATED FILES
//   MainViewModel.cs       — holds all VM refs; drives the 1 s / 5 s refresh timer
//   Views/MainWindow.xaml  — nav sidebar and CurrentView host (ContentControl)
//   Core/CrashGuard.cs     — sentinel-file crash detection; watchdog heartbeat every tick
// ════════════════════════════════════════════════════════════════════════════

using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Systema.Core;
using Systema.Services;
using Systema.ViewModels;
using Systema.Views;

namespace Systema;

public partial class App : Application
{
    private static readonly LoggerService Log = LoggerService.Instance;

    // Prevents ShowCrashOnUIThread from being invoked recursively if the crash
    // window itself triggers another unhandled exception on the dispatcher.
    // Uses int + Interlocked to make the check-and-set atomic (volatile bool cannot).
    private static int _crashHandlerActiveInt;

    // Services held at App level so they outlive any single window
    private TrayService?     _trayService;
    private MainWindow?      _mainWindow;
    private MainViewModel?   _mainVm;
    private UpdateService?   _updateService;
    private HeartbeatService? _heartbeat;
    // Held so the crash / process-exit handlers can restore napped processes (incl. lifting CPU caps)
    // before Systema's handles close — preventing orphaned throttles on every exit path we can run on.
    private TaskSleepViewModel? _taskSleepVm;
    private int _napsRestoredOnShutdown;   // Interlocked guard so the restore runs at most once

    // Single-instance guard — prevents the watchdog task from spawning duplicates
    private static Mutex? _singleInstanceMutex;

    // Cross-process "show the window" signal. The owning instance waits on this named event; a
    // later normal launch (a user double-clicking Systema) sets it so the running instance pops
    // its window instead of the launch exiting silently. Without this, if the tray icon isn't
    // visible the user is locked out and has to force-quit. Name is process-global.
    private const  string ShowWindowEventName = "Global\\SystemaShowWindow";
    private static EventWaitHandle? _showWindowSignal;

    // True once the main window or tray icon has been shown successfully. Any
    // exception caught BEFORE this point must shut the process down (instead of
    // leaving a zombie that holds the single-instance mutex and prevents future
    // launches until the user reinstalls).
    private bool _startupCompleted;

    /// <summary>
    /// Installs the running exe into Program Files\Systema and launches the installed
    /// copy. Backs the <c>--install</c> flag and the first-run install prompt. The app
    /// already auto-elevates (manifest requireAdministrator), so this runs as admin.
    /// </summary>
    private void RunSelfInstall(bool silent)
    {
        string version = UpdateService.GetCurrentVersionString();
        Log.Info("App", $"=== --install: self-installing v{version} (silent={silent}) ===");

        string? installed = SelfInstallService.Install(silent, version);
        if (installed != null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = installed,
                    Arguments       = silent ? "--silent" : string.Empty,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log.Warn("App", $"Post-install launch failed: {ex.Message}"); }

            if (!silent)
                MessageBox.Show(
                    "Systema has been installed.\n\nYou'll find it in your Start Menu and on your Desktop.",
                    "Systema", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!silent)
        {
            MessageBox.Show(
                "Install failed. Please run Systema as administrator and try again.",
                "Systema", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Wire all global exception handlers before anything else ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // Last-resort cleanup: fires on graceful exit, Environment.Exit, and most managed
        // terminations — restores napped processes (and lifts their CPU caps) before our handles
        // close, so they aren't left orphaned. A hard TerminateProcess / power loss skips this; the
        // startup nap-recovery in TaskSleepService covers what it can in that case.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreNapsOnShutdown();

        base.OnStartup(e);

        // Keep the app alive even when no window is visible (tray mode)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ── Uninstall cleanup mode ──────────────────────────────────────────────
        // Triggered by Inno Setup [UninstallRun]: Systema.exe --cleanup
        // Runs BEFORE the installer deletes files so all services are available.
        // Boot-time re-enforcement, invoked by the SystemaCoreParking scheduled task.
        // Headless: no mutex, no UI, no CrashGuard. The task used to run a hardcoded powercfg
        // string, which drifted from the code as soon as the setting list grew; routing it
        // through the service means the task can never enforce a stale value again.
        if (e.Args.Contains("--reapply-parking"))
        {
            Log.Info("App", "=== --reapply-parking: re-enforcing core parking at boot ===");
            if (AdminCheckService.IsAdmin())
                new CoreParkingService().ReapplyCoreParkingAsync().GetAwaiter().GetResult();
            else
                Log.Warn("App", "Core parking re-apply skipped — not running as administrator");
            Shutdown(0);
            return;
        }

        // Skips single-instance mutex, CrashGuard, UI, and everything else.
        if (e.Args.Contains("--cleanup"))
        {
            Log.Info("App", "=== --cleanup: restoring Windows settings before uninstall ===");
            if (AdminCheckService.IsAdmin())
                UninstallCleanupService.RunCleanup();
            else
                Log.Warn("App", "Cleanup skipped — not running as administrator");
            Shutdown(0);
            return;
        }

        // ── Self-install / uninstall (SAC-safe install path) ────────────────────
        // Systema.exe can install itself, so machines with Smart App Control enforced
        // — where the Inno installer's temp-extracted engine is blocked (Error 4551:
        // "Application Control policy has blocked this file") — can still install. The
        // Inno installer and the auto-updater are UNCHANGED, so existing users are
        // unaffected: they're already at the canonical Program Files\Systema path and
        // IsRunningInstalled() short-circuits all of this for them.
        if (e.Args.Contains("--uninstall"))
        {
            Log.Info("App", "=== --uninstall: removing Systema ===");
            SelfInstallService.Uninstall();
            Shutdown(0);
            return;
        }
        // Inno-style silent flags — the EXISTING (already-shipped) auto-updater
        // downloads the release's .exe asset and runs it with
        // "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-". If that asset happens to be
        // this self-installer (instead of the Inno setup), honour those flags as a
        // silent self-install. This makes auto-update from OLDER versions work no
        // matter which .exe asset their updater picks.
        bool innoSilent = e.Args.Any(a =>
            a.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/SILENT",     StringComparison.OrdinalIgnoreCase));

        if (e.Args.Contains("--install") || innoSilent)
        {
            RunSelfInstall(silent: innoSilent || e.Args.Contains("--silent"));
            Shutdown(0);
            return;
        }
        // Double-click of a not-yet-installed copy (e.g. the downloaded exe on the
        // Desktop) → offer to install. Declining runs it portably. Skipped for the
        // installed copy and for silent / tray / --portable starts.
        if (!SelfInstallService.IsRunningInstalled()
            && !e.Args.Contains("--portable")
            && !e.Args.Contains("--silent")
            && !e.Args.Contains("--autostart"))
        {
            var choice = MessageBox.Show(
                "Install Systema on this PC?\n\n" +
                "This copies Systema into Program Files and adds Start Menu and Desktop " +
                "shortcuts so it works like a normally-installed app.\n\n" +
                "Click No to just run Systema this once without installing.",
                "Systema Setup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel) { Shutdown(0); return; }
            if (choice == MessageBoxResult.Yes)
            {
                RunSelfInstall(silent: false);
                Shutdown(0);
                return;
            }
            // No → fall through and run portably.
        }

        Log.Info("App", "Starting Systema...");

        // ── Single-instance guard ──
        // Normally another running instance owns the mutex and we exit. But if
        // that running instance has WEDGED (timer deadlock, COM call hang, etc.)
        // the mutex stays held forever and the user thinks "Systema won't open
        // until I reinstall." HeartbeatService writes a timestamp file every
        // ~10 s — if it's stale, the running instance is dead, so we kill it
        // and reclaim the mutex instead of giving up.
        _singleInstanceMutex = new Mutex(true, "Global\\SystemaSingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            if (HeartbeatService.IsHeartbeatStale())
            {
                int killed = HeartbeatService.KillHungInstances();
                Log.Warn("App",
                    $"Existing Systema instance is unresponsive (heartbeat stale) — killed {killed} process(es); reclaiming the single-instance mutex");
                try { _singleInstanceMutex.Dispose(); } catch { }
                // Brief settle so the OS finishes tearing down the killed process
                // and releases the mutex's kernel object reference.
                System.Threading.Thread.Sleep(500);
                _singleInstanceMutex = new Mutex(true, "Global\\SystemaSingleInstance", out isNewInstance);
            }

            if (!isNewInstance)
            {
                // A normal (user-initiated) launch means "open Systema" — tell the running instance
                // to show its window before we exit, so the user isn't stuck when the tray icon is
                // missing. A --silent / --autostart duplicate (boot / watchdog) must NOT pop a window.
                bool silentDuplicate = e.Args.Contains("--silent") || e.Args.Contains("--autostart");
                if (!silentDuplicate)
                {
                    try
                    {
                        if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var sig))
                        {
                            sig.Set();
                            sig.Dispose();
                            Log.Info("App", "Another instance is already running — signaled it to show its window; exiting");
                        }
                        else
                        {
                            Log.Warn("App", "Running instance alive but show-window signal not found — exiting without surfacing it");
                        }
                    }
                    // Called out separately from the generic catch: this is what a DACL mismatch
                    // looks like (the event exists but this process may not open it), and it is
                    // the one failure that leaves the user with no way in except the tray icon.
                    catch (UnauthorizedAccessException ex)
                    {
                        Log.Warn("App",
                            "Show-window signal exists but access was denied — the running instance was started " +
                            $"by a different/elevated context, so its window could not be surfaced: {ex.Message}");
                    }
                    catch (Exception ex) { Log.Warn("App", $"Could not signal running instance to show: {ex.Message}"); }
                }
                else
                {
                    Log.Info("App", "Another instance is already running and is alive (silent duplicate) — exiting");
                }
                _singleInstanceMutex.Dispose();
                Shutdown(0);
                return;
            }
        }

        // We're the owning instance — start the heartbeat so a future duplicate
        // launch can detect us and only kill us if we've actually hung.
        _heartbeat = new HeartbeatService();
        _heartbeat.Start();

        // Own the "show the window" signal and wait on it: whenever a later normal launch sets it,
        // surface our window on the UI thread. This is the reliable way back in when the tray icon
        // didn't register at boot — a plain relaunch now opens Systema instead of silently exiting.
        try
        {
            // Create the signal with an EXPLICIT DACL. Systema runs elevated, and a kernel object
            // created by an elevated process gets a default DACL that a lower-integrity process
            // cannot open — TryOpenExisting throws UnauthorizedAccessException rather than simply
            // returning false. Any launch that is not elevated (a shortcut whose UAC prompt was
            // declined, a shell handler, a future non-admin build) could therefore never surface
            // the running window. Granting Authenticated Users modify+synchronize removes that
            // failure mode entirely; the event only ever carries "show your window", so it is not
            // a privileged capability.
            var signalSecurity = new EventWaitHandleSecurity();
            signalSecurity.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
                AccessControlType.Allow));

            _showWindowSignal = EventWaitHandleAcl.Create(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: ShowWindowEventName,
                createdNew: out _,
                eventSecurity: signalSecurity);
            var waiter = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showWindowSignal.WaitOne();
                        Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                    }
                    catch { break; }
                }
            })
            { IsBackground = true, Name = "SystemaShowWindowWaiter" };
            waiter.Start();
        }
        catch (Exception ex) { Log.Warn("App", $"Show-window signal setup failed: {ex.Message}"); }

        // ── Check for crash from previous session ──
        // CrashGuard writes a sentinel file before risky operations and deletes it
        // when they complete. If it still exists → previous session crashed mid-operation.
        var previousCrash = CrashGuard.CheckPreviousCrash();
        if (previousCrash != null)
        {
            // The report is always archived to disk by CheckPreviousCrash(). Only pop the
            // modal on a normal (visible) launch — a --silent / --autostart start (incl. the
            // ghost-hang AUTO-RESTART) recovers quietly to the tray without stealing focus.
            bool silentStart = e.Args.Contains("--silent") || e.Args.Contains("--autostart");
            if (silentStart)
                Log.Warn("App", "Previous session crash detected — report archived (silent start, dialog suppressed)");
            else
            {
                Log.Warn("App", "Previous session crash detected — showing report");
                CrashReportWindow.ShowPreviousCrash(previousCrash);
            }
        }

        // ── Start CrashGuard watchdog ──
        CrashGuard.Start();

        if (!AdminCheckService.IsAdmin())
        {
            Log.Warn("App", "Not running as administrator — aborting startup");
            MessageBox.Show(
                "Systema requires administrator privileges to function correctly.\n\n" +
                "Please right-click Systema.exe and select 'Run as Administrator'.",
                "Administrator Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            CrashGuard.Stop();
            Shutdown();
            return;
        }

        Log.Info("App", "Admin check passed — composing services");

        // Log system hardware info to the session log so any user's log file
        // is self-contained — no need to ask for a separate diagnostic report.
        Log.LogSystemInfo();

        try
        {
            // ── Manual DI composition root ──
            var settingsService    = new SettingsService();
            var memoryService      = new MemoryService();
            var startupService     = new StartupService();
            var telemetryService   = new TelemetryService();
            var animationService   = new AnimationService();
            var powerPlanService   = new PowerPlanService();
            var processService     = new ProcessService();
            var restoreService     = new RestorePointService();
            var serviceControl     = new ServiceControlService();
            var optFeatures        = new OptionalFeaturesService();
            var dnsService         = new DnsService();
            var processLassoService = new ProcessLassoService();
            var batteryPauseService = new BatteryPauseService();
            var gameboosterService  = new GameBoosterService(serviceControl, settingsService, processLassoService, batteryPauseService);
            var realtekService      = new RealtekCleanerService();
            var coreParkingService  = new CoreParkingService();
            var thermalService      = new ThermalManagementService();
            var wuTweaksService     = new WindowsUpdateTweaksService();

            var stabilityService    = new SystemStabilityService();
            var win11CleanupService = new Win11CleanupService();
            var graphicsTweaks      = new GraphicsTweaksService();
            var bloatwareService    = new BloatwareService();
            var intelGpuService     = new IntelGpuService();
            var nvidiaGpuService    = new NvidiaGpuService();
            _updateService          = new UpdateService(settingsService);
            var watchdogService     = new WatchdogService();
            var healthService       = new HealthScoreService(
                memoryService, startupService, telemetryService,
                animationService, powerPlanService);

            Log.Info("App", "All services instantiated");

            // ── Core parking re-enforcement on startup ──
            // The SystemaCoreParking scheduled task runs at boot as SYSTEM, but
            // SYSTEM's active power scheme often differs from the signed-in user's,
            // so the boot-time apply silently no-ops and core parking "only applies
            // once." Re-apply it from the running (user-context, elevated) app ~20s
            // after launch so the user's active scheme is corrected after every
            // reboot or third-party power-plan reset. The 20 s delay keeps it off
            // the critical startup path.
            if (settingsService.CoreParkingEnabled)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(20_000);
                        await coreParkingService.ReapplyCoreParkingAsync();
                    }
                    catch (Exception ex) { Log.Warn("App", $"Startup core-parking re-apply failed: {ex.Message}"); }
                });
            }

            // Applying at startup and boot is not enough on its own: every value is written to
            // the ACTIVE plan, so switching plans lands on an unconfigured one and parking quietly
            // reverts. Unplugging can switch plans, Max Life switches to Power Saver deliberately,
            // and vendor utilities swap them too. This re-applies whenever the plan changes.
            // Started unconditionally: it checks the setting on every tick, so it costs nothing
            // while the feature is off and needs no restart if it is switched on later.
            coreParkingService.StartPlanWatch(() => settingsService.CoreParkingEnabled);

            // ── Windows 11 nag reinforcement ──
            // Disable Suggestions defaults ON, so on first run this applies it; on
            // every later launch it re-asserts the HKCU values a feature update may
            // have reset (the "never come back" reinforcement). Web search is opt-in
            // and only re-applied when the user turned it on. Both are HKCU-only and
            // run off the startup critical path.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (settingsService.DisableSuggestionsEnabled)
                        await win11CleanupService.DisableConsumerContentAsync();
                    if (settingsService.DisableWebSearchEnabled)
                        await win11CleanupService.DisableWebSearchAsync();
                }
                catch (Exception ex) { Log.Warn("App", $"Win11 nag reinforcement failed: {ex.Message}"); }
            });

            // ── Sleep → Hibernate reinforcement (periodic) ──
            // A Windows Update, a power-plan switch (including Systema's own), or an OEM power tool can
            // wipe the HIBERNATEIDLE timeout, so the setting "stops working" while the app still shows it
            // as on. The old version reinforced ONCE ~15 s after launch — but Systema is kept alive by the
            // watchdog for days/weeks, so the startup path never re-fired and drift that happened mid-run
            // (e.g. a monthly update ~a week later) was never corrected. Reinforce shortly after launch AND
            // every 30 minutes thereafter, reading the saved choice live each pass so a mid-session toggle
            // is honoured without a restart. The drift check inside ReinforceSleepToHibernateAsync makes
            // each pass a cheap no-op (two powercfg /query calls) when nothing has changed, and re-applies
            // to the CURRENT scheme so a plan switch is caught within one interval. Runs off the critical
            // startup path; the loop lives for the process lifetime.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(15_000); // off the critical startup path
                while (true)
                {
                    try
                    {
                        if (settingsService.SleepToHibernateEnabled || settingsService.SleepToHibernateAcEnabled)
                            await stabilityService.ReinforceSleepToHibernateAsync(
                                settingsService.SleepToHibernateEnabled,   settingsService.SleepToHibernateMinutes,
                                settingsService.SleepToHibernateAcEnabled, settingsService.SleepToHibernateAcMinutes);
                    }
                    catch (Exception ex) { Log.Warn("App", $"Sleep-to-Hibernate reinforcement failed: {ex.Message}"); }
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(30));
                }
            });

            // ── First-run defaults ──
            // Enable "Start with Windows" automatically on first launch so the app
            // is available in the background without the user having to opt-in.
            ApplyFirstRunDefaults(settingsService);

            // ── Watchdog self-heal ──
            // If the user has "Keep Systema Running" enabled but the Task Scheduler task
            // was wiped by an update or OS reset, silently re-create it now.
            if (settingsService.KeepSystemaRunning && !watchdogService.IsEnabled)
            {
                try
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                    watchdogService.Enable(exePath);
                    Log.Info("App", "Watchdog task re-registered (was missing on startup)");
                }
                catch (Exception ex)
                {
                    Log.Warn("App", $"Could not re-register watchdog on startup: {ex.Message}");
                }
            }

            // TaskSleepViewModel must be created before DashboardViewModel (dashboard reads its live process list)
            var taskSleepVm   = new TaskSleepViewModel();
            _taskSleepVm = taskSleepVm;   // so crash / process-exit handlers can restore naps before exit

            var dashboardVm   = new DashboardViewModel(
                gameboosterService, taskSleepVm, serviceControl,
                memoryService, dnsService, powerPlanService,
                wuTweaksService, coreParkingService, settingsService, optFeatures, stabilityService,
                graphicsTweaks, thermalService);

            var memoryVm      = new MemoryViewModel(memoryService, startupService, settingsService);
            var servicesVm    = new ServicesViewModel(serviceControl, optFeatures, restoreService, settingsService, gameboosterService);
            var visualVm      = new VisualViewModel(animationService, powerPlanService, settingsService);
            var gameBoosterVm = new GameBoosterViewModel(gameboosterService, settingsService);
            var settingsVm    = new SettingsViewModel(settingsService, restoreService, _updateService, watchdogService, gameboosterService);
            var toolsVm       = new ToolsViewModel(
                realtekService, coreParkingService, restoreService,
                settingsService, dnsService, wuTweaksService, stabilityService,
                win11CleanupService);
            var bloatwareVm   = new BloatwareViewModel(bloatwareService, restoreService, settingsService);
            var graphicsVm    = new GraphicsViewModel(graphicsTweaks, settingsService);
            var audioVm       = new AudioViewModel(new AudioService());
            var intelVm       = new IntelGpuViewModel(intelGpuService, settingsService);
            var nvidiaVm      = new NvidiaGpuViewModel(nvidiaGpuService, settingsService);
            var dellVm        = new DellViewModel(thermalService, settingsService, powerPlanService);

            _mainVm = new MainViewModel(dashboardVm, memoryVm, servicesVm,
                                        visualVm, gameBoosterVm, settingsVm, toolsVm, taskSleepVm, bloatwareVm, graphicsVm, audioVm, intelVm, nvidiaVm, dellVm);

            // NOTE: Graphics tweaks are intentionally reflect-only — Systema NEVER changes
            // them on launch. The Graphics tab reads the live Windows state and only writes
            // when the user flips a toggle, so it stays in sync with Windows Settings /
            // manual registry edits in both directions.

            // ── Intel iGPU profile re-apply on startup (opt-in) ──
            // Intel driver updates sometimes wipe the display-adapter registry values. When
            // the user opted in, re-apply the saved profile ~20 s after launch. Writes go to
            // the ACTIVE adapter only (IntelGpuService.WriteValue → PrimaryAdapter).
            if (settingsService.IntelGpuReapplyEnabled && intelVm.IsIntelPresent)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(20_000);
                        var saved = settingsService.IntelGpuProfile;
                        if (saved is { Count: > 0 })
                        {
                            var adapters = intelGpuService.DetectIntelAdapters();
                            foreach (var (name, value) in saved)
                                intelGpuService.WriteValue(adapters, name, value);
                            Log.Info("App", $"Re-applied saved Intel iGPU profile ({saved.Count} value(s)) after startup.");
                        }
                    }
                    catch (Exception ex) { Log.Warn("App", $"Startup Intel iGPU re-apply failed: {ex.Message}"); }
                });
            }

            // ── NVIDIA power-management re-apply on startup (opt-in) ──
            // NVIDIA driver updates can wipe the PowerMizer values. When the user opted in
            // and chose "prefer maximum performance", re-write it ~20 s after launch to the
            // PRESENT adapter only (NvidiaGpuService targets present adapters).
            if (settingsService.NvidiaGpuReapplyEnabled && nvidiaVm.IsNvidiaPresent
                && settingsService.NvidiaGpuPreferMaxPerformance)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(20_000);
                        var adapters = nvidiaGpuService.DetectNvidiaAdapters();
                        nvidiaGpuService.SetPowerSaving(adapters, on: false);
                        Log.Info("App", "Re-applied NVIDIA 'prefer maximum performance' after startup.");
                    }
                    catch (Exception ex) { Log.Warn("App", $"Startup NVIDIA re-apply failed: {ex.Message}"); }
                });
            }

            // ── Timer-resolution hold (opt-in) ──
            // GlobalTimerResolutionRequests persists in the registry, but the actual 0.5 ms
            // request must be re-issued by a running process each boot and kept pinned. If the
            // user opted in, start the hold now — this changes no setting, it just honours it.
            if (graphicsTweaks.IsTimerResolutionForced())
            {
                graphicsTweaks.StartTimerResolutionHold();
                Log.Info("App", "Started 0.5 ms timer-resolution hold (user opted in).");
            }

            // ── One-time seed: adopt already-applied disable tweaks as intent ──
            // For users who set MPO / TdrDelay / Game DVR before reinforcement existed, adopt those
            // deliberately-tweaked states as their saved intent once, so they get reinforced without a
            // re-toggle. Only the clear non-default "disable/extend" states are seeded — HAGS and
            // windowed-opts are left unarmed until the user explicitly flips them (they're on/off mirrors
            // of Windows' own settings, so we don't want to adopt a plain default as "intent").
            if (!settingsService.GraphicsIntentSeeded)
            {
                // NOT on NVIDIA: a disabled MPO there is almost certainly Systema's own doing
                // (Auto-Pilot applied it unconditionally until 0.7.278), and adopting it as
                // "user intent" is what made reinforcement keep restoring a broken VSync path.
                if (graphicsTweaks.IsMpoDisabled() && !graphicsTweaks.IsMpoAutoDisableUnsafe())
                    settingsService.GraphicsMpoDisabled = true;
                if (graphicsTweaks.IsTdrDelayExtended()) settingsService.GraphicsTdrExtended     = true;
                if (graphicsTweaks.IsGameDvrDisabled())  settingsService.GraphicsGameDvrDisabled = true;
                settingsService.GraphicsIntentSeeded = true;
                Log.Info("App", "Seeded graphics disable-tweak intents from current state (first run).");
            }

            // ── Graphics tweaks reinforcement ──
            // A GPU driver update or Windows feature update can silently reset MPO / HAGS / TdrDelay, and
            // Windows re-enables Game DVR — so a toggle the user set "stops working" until they re-toggle
            // it. Re-assert only the choices the user actually made, and only when the live value drifted
            // (never on a fresh install). MPO/TdrDelay are left to Auto-Pilot while it's on. Delayed off
            // the critical startup path.
            if (settingsService.GraphicsMpoDisabled || settingsService.GraphicsTdrExtended
                || settingsService.GraphicsGameDvrDisabled || settingsService.GraphicsHagsPref >= 0
                || settingsService.GraphicsWindowedOptPref >= 0)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await System.Threading.Tasks.Task.Delay(18_000);
                        graphicsTweaks.ReinforceGraphicsFromIntent(
                            settingsService.GraphicsMpoDisabled,   settingsService.GraphicsHagsPref,
                            settingsService.GraphicsTdrExtended,   settingsService.GraphicsWindowedOptPref,
                            settingsService.GraphicsGameDvrDisabled, settingsService.AutoPilotModeEnabled);
                    }
                    catch (Exception ex) { Log.Warn("App", $"Graphics reinforcement failed: {ex.Message}"); }
                });
            }

            Log.Info("App", "All ViewModels constructed");

            // ── Wire GameBooster → TaskSleep game-mode suppression ──
            // When a game is detected (or manual boost starts), tell TaskSleep to stop
            // giving idle wakes to background processes so the CPU stays free for the game.
            // Also pause the auto-updater during game sessions so it never installs mid-game.
            gameboosterService.BoostActivated   += _ => { taskSleepVm.SetGameMode(true);  _updateService.IsGameModeActive = true;  };
            gameboosterService.BoostDeactivated += () => { taskSleepVm.SetGameMode(false); _updateService.IsGameModeActive = false; };

            // ── Tray setup ──
            _trayService = new TrayService();
            _trayService.ShowWindowRequested += ShowMainWindow;
            _trayService.ExitRequested       += ExplicitShutdown;

            // ── Tray "Toggle Game Boost" ──
            // Right-click the tray icon → start/stop Game Boost without opening the
            // window. Toggles MANUAL boost: if any boost is currently active (manual
            // OR game-detected), the click stops it; otherwise it starts a manual boost.
            _trayService.ToggleBoostRequested += () =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (gameboosterService.BoostActive)
                        {
                            await gameboosterService.DisableManualBoostAsync();
                        }
                        else
                        {
                            await gameboosterService.EnableManualBoostAsync();
                            // EnableManualBoostAsync silently no-ops when the Game Booster
                            // master switch is off — give the user feedback instead of a
                            // dead click they can't diagnose from the tray.
                            if (!gameboosterService.BoostActive)
                                _trayService?.ShowBalloon("Game Boost",
                                    "Game Booster is turned off. Open Systema → Game Boost to enable it.",
                                    System.Windows.Forms.ToolTipIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("App", $"Tray toggle-boost failed: {ex.Message}");
                    }
                });
            };

            // Keep the tray menu caption ("Start"/"Stop Game Boost") + checkmark in
            // sync with the real boost state, regardless of what triggered the change.
            gameboosterService.BoostActivated   += _  => _trayService?.UpdateBoostMenuState(true);
            gameboosterService.BoostDeactivated += () => _trayService?.UpdateBoostMenuState(false);
            // Reflect whatever state we start in (e.g. relaunched mid-boost).
            _trayService.UpdateBoostMenuState(gameboosterService.BoostActive);

            // ── Task Sleep row: toggle the engine and show the live napped count ──
            _trayService.ToggleTaskSleepRequested += () =>
            {
                try
                {
                    taskSleepVm.IsEnabled = !taskSleepVm.IsEnabled;
                    _trayService?.UpdateTaskSleepMenuState(taskSleepVm.IsEnabled);
                }
                catch (Exception ex) { Log.Warn("App", $"Tray Task Sleep toggle failed: {ex.Message}"); }
            };
            _trayService.UpdateTaskSleepMenuState(taskSleepVm.IsEnabled);

            // Task Sleep can also be toggled from the window, so re-read the state as the menu
            // opens rather than polling — no background work for a menu nobody has opened.
            _trayService.MenuOpening += () =>
                _trayService?.UpdateTaskSleepMenuState(taskSleepVm.IsEnabled);

            // ── Power plan submenu ──
            _trayService.PowerPlanRequested += plan =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (plan == "performance") await powerPlanService.SetHighPerformanceAsync();
                        else                       await powerPlanService.SetBalancedAsync();
                        Log.Info("App", $"Power plan changed from the tray menu: {plan}");
                    }
                    catch (Exception ex) { Log.Warn("App", $"Tray power-plan change failed: {ex.Message}"); }
                });
            };

            // ── Check for updates ──
            _trayService.CheckForUpdatesRequested += () =>
            {
                _ = Task.Run(async () =>
                {
                    try { await _updateService!.CheckNowAsync(); }
                    catch (Exception ex) { Log.Warn("App", $"Tray update check failed: {ex.Message}"); }
                });
            };

            // Start background game monitoring (passes tray ref for balloon notifications)
            gameboosterService.StartMonitoring(_trayService);

            // ── Auto-updater ──
            // Starts the background loop: checks on startup (20 s delay), re-checks
            // every 2 days, and installs silently when CPU has been idle for 5 minutes.
            // PreShutdownAsync fires first so the system is cleanly restored before the
            // installer replaces Systema.exe on disk: it deactivates Game Boost (if active)
            // and pauses Task Sleep (restoring every napped process). ShutdownRequested then
            // fires just before the installer launches.
            _updateService.PreShutdownAsync = async () =>
            {
                if (gameboosterService.BoostActive)
                {
                    Log.Info("App", "Pre-update: Game Boost is active — deactivating before installer launches");
                    await gameboosterService.DeactivateForUpdateAsync();
                    Log.Info("App", "Pre-update: Game Boost deactivated");
                }

                // Restore every napped process (priority / RAM / EcoQoS / CPU-cap) before this
                // process dies. Those throttles persist on the target processes after Systema
                // exits, and the freshly-installed version has no record of what was napped — so
                // without this they'd stay stuck at Idle / lowest-RAM. PauseForUpdate keeps the
                // IsEnabled setting intact, so the new version re-enables Task Sleep on startup.
                try { taskSleepVm.PauseForUpdate(); }
                catch (Exception ex) { Log.Warn("App", $"Pre-update: Task Sleep restore failed: {ex.Message}"); }
                Log.Info("App", "Pre-update: Task Sleep restored — proceeding with update install");
            };
            _updateService.ShutdownRequested += () =>
            {
                // FORCE-EXIT SAFETY NET. The graceful Shutdown(0) below can wedge — a slow
                // watchdog/Task-Scheduler call, the tray teardown, or any lingering work on the
                // UI thread — and because the window never truly closes (Close → Hide) and the
                // app is OnExplicitShutdown, a stuck UI thread leaves the process alive. That
                // keeps Systema.exe LOCKED, so the installer can't replace it: the update appears
                // to "freeze" and never relaunches. This timer runs OFF the UI thread, so even a
                // fully wedged UI can't stop it — it hard-exits after a few seconds, freeing the
                // exe so the installer finishes and its silent relaunch fires.
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(4000);
                    Log.Warn("App", "Update shutdown didn't finish in 4 s — forcing exit so the installer can replace the exe and relaunch");
                    Environment.Exit(0);
                });

                Dispatcher.Invoke(() =>
                {
                    Log.Info("App", "Auto-updater requesting shutdown to apply update");
                    // Disable the watchdog BEFORE shutting down so it cannot relaunch Systema
                    // while the installer is replacing the exe on disk.
                    try { watchdogService.Disable(); }
                    catch (Exception ex) { Log.Warn("App", $"Could not disable watchdog before update: {ex.Message}"); }
                    CrashGuard.Stop();
                    _trayService?.Dispose();
                    Shutdown(0);
                });
            };
            _updateService.StartAutoUpdate();

            // "--silent" or "--autostart" → tray-only (Ghost Mode); else show window immediately
            bool silent = e.Args.Contains("--silent") || e.Args.Contains("--autostart");
            if (silent)
            {
                Log.Info("App", "Silent startup — entering Ghost Mode, tray only");
                _trayService.EnterGhostMode();
                _trayService.ShowBalloon("Systema", "Running in the background. Double-click to open.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            else
            {
                ShowMainWindow();
            }

            // From here on, a UI-thread exception is a "running app" crash — we
            // let OnDispatcherUnhandledException decide what to do. Before this
            // point ANY thrown exception must shut the process down or it lingers
            // as a zombie holding the single-instance mutex.
            _startupCompleted = true;
        }
        catch (Exception ex)
        {
            Log.Fatal("App", "Startup composition failed", ex);
            ShowCrashOnUIThread(ex, "Application Startup");

            // CRITICAL: don't leave a zombie holding the single-instance mutex.
            // Without this Shutdown, the process keeps running invisibly (no
            // window, no tray — ShutdownMode is OnExplicitShutdown), the mutex
            // stays acquired, and every future double-click of the shortcut
            // silently exits at the isNewInstance check above. That's the
            // "Systema won't start until I reinstall" bug.
            try { CrashGuard.Stop(); } catch { }
            Shutdown(1);
        }
    }

    // ── Show / Hide main window ────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        // A show request can race ahead of startup (a duplicate launch in the first moments).
        // The view-model isn't built yet then, so there's nothing to show — ignore it.
        if (_mainVm == null) return;

        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_mainVm);
            // MainWindow.OnClosing turns every close into a hide, so this should only ever fire
            // on a real exit. Belt and braces: a closed WPF Window can never be shown again, so
            // if anything does manage to close it, drop the reference and build a fresh one on
            // the next request rather than throwing "Cannot set Visibility ... after a Window
            // has closed" — which is precisely how this crashed for a user on 0.7.279.
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        _trayService?.ExitGhostMode();

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();

        Log.Info("App", "MainWindow shown");
    }

    /// <summary>Called by MainWindow when the user hides it (minimize/close to tray).</summary>
    public void NotifyWindowHidden()
    {
        _trayService?.EnterGhostMode();
        Log.Info("App", "Window hidden — Ghost Mode active");
    }

    private void ExplicitShutdown()
    {
        Log.Info("App", "User requested exit from tray");
        CrashGuard.Stop();
        _heartbeat?.Dispose();
        _mainVm?.Dispose();
        _trayService?.Dispose();
        if (_mainWindow != null) _mainWindow.AllowClose = true;   // the one close that is real
        _mainWindow?.Close();
        _updateService?.StopAutoUpdate();
        Shutdown(0);
    }

    // ── Dispatcher (UI thread) unhandled exceptions ──
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsWpfShutdownTelemetryError(e.Exception))
        {
            Log.Warn("Dispatcher", "Suppressed harmless WPF telemetry error during Windows session end");
            e.Handled = true;
            return;
        }

        e.Handled = true;
        Log.Fatal("Dispatcher", "UI thread unhandled exception", e.Exception);

        // Also write to CrashGuard so the report persists even if ShowCrash fails
        CrashGuard.Mark($"UI EXCEPTION: {e.Exception.GetType().Name}: {e.Exception.Message}");

        // XAML/layout exceptions re-fire on every WPF layout pass — CrashReportWindow
        // (a WPF window itself) cannot render when the layout engine is in a crash loop.
        // Use a Win32 MessageBox which bypasses WPF rendering entirely.
        // Treat as "WPF render is broken" — bypass CrashReportWindow (a WPF Window)
        // entirely and show a Win32 MessageBox. Without this, a font / layout
        // exception fires every frame: opening the crash window re-triggers the
        // measure pass, throws again, and the user sees "could not display the
        // crash report" instead of the real stack trace.
        bool isXamlError = e.Exception is System.Windows.Markup.XamlParseException
            || e.Exception.InnerException is System.Windows.Markup.XamlParseException
            || e.Exception is InvalidOperationException
               && (e.Exception.Message.Contains("TargetType") || e.Exception.Message.Contains("Style"))
            // Layout-pass crashes (font lookup, glyph cache, typeface fallback) —
            // anything thrown inside Measure/Arrange/Render will re-fire when the
            // crash window itself measures, so we can't use a WPF window to show it.
            || HasLayoutFrame(e.Exception);

        if (isXamlError)
        {
            string detail = e.Exception.InnerException?.Message ?? e.Exception.Message;
            MessageBox.Show(
                $"Systema encountered a UI rendering error and needs to close.\n\n" +
                $"{detail}\n\n" +
                $"The full crash report has been saved. Please report this issue.",
                "Systema — Fatal UI Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            CrashGuard.Stop();
            Shutdown(1);
            return;
        }

        ShowCrashOnUIThread(e.Exception, "UI Thread Exception");

        // If the crash happened BEFORE the main window or tray icon finished
        // coming up, the user has no way to interact with us — without this
        // shutdown the process would linger as a zombie holding the
        // single-instance mutex (the "Systema won't start until I reinstall"
        // bug). After startup we let the app keep running so a transient WPF
        // glitch doesn't kill a live session.
        if (!_startupCompleted)
        {
            Log.Warn("App", "UI exception fired before startup completed — shutting down to release single-instance mutex");
            try { CrashGuard.Stop(); } catch { }
            Shutdown(1);
        }
    }

    /// <summary>
    /// True when the exception originated inside the WPF layout / render pipeline
    /// (Measure, Arrange, GlyphTypeface, TextFormatter, MediaContext, etc.).
    /// These crashes re-fire on every layout pass, so opening a WPF crash window
    /// is unsafe — it just hits the same exception while measuring itself.
    /// </summary>
    private static bool HasLayoutFrame(Exception? ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            var st = cur.StackTrace;
            if (st == null) continue;
            if (st.Contains("MeasureOverride")
             || st.Contains("ArrangeOverride")
             || st.Contains("ContextLayoutManager")
             || st.Contains("MediaContext.Render")
             || st.Contains("GlyphTypeface")
             || st.Contains("TextInterface.Font"))
                return true;
        }
        return false;
    }

    private static bool IsWpfShutdownTelemetryError(Exception? ex)
    {
        if (ex == null) return false;
        if (ex is not System.IO.FileNotFoundException fnfe) return false;

        bool isTracingAssembly = fnfe.FileName?.Contains("System.Diagnostics.Tracing") == true;
        bool isFromShutdown    = ex.StackTrace is { } st &&
                                 (st.Contains("ControlsTraceLogger") ||
                                  st.Contains("WmQueryEndSession")   ||
                                  st.Contains("CriticalShutdown"));
        return isTracingAssembly || isFromShutdown;
    }

    // ── AppDomain (non-UI thread) unhandled exceptions ──
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal("AppDomain", $"Non-UI thread crash (terminating={e.IsTerminating})", ex);

        // Write to disk before trying to show UI (UI might not work if process is terminating)
        CrashGuard.Mark($"DOMAIN EXCEPTION: {ex?.GetType().Name}: {ex?.Message}");

        // The process is about to die — restore napped processes (and lift their CPU caps) NOW, while
        // we still can. ProcessExit isn't guaranteed to run on a fatal unhandled exception.
        if (e.IsTerminating) RestoreNapsOnShutdown();

        if (Dispatcher != null && !Dispatcher.CheckAccess())
            Dispatcher.Invoke(() => ShowCrashOnUIThread(ex, "Background Thread Exception"));
        else
            ShowCrashOnUIThread(ex, "Background Thread Exception");
    }

    /// <summary>
    /// Restores every napped process (priority / memory / EcoQoS / GPU / affinity) and lifts their
    /// kernel CPU caps before Systema's handles close, so a crash or process exit doesn't leave them
    /// orphaned. Runs at most once (Interlocked guard) since several exit paths may call it.
    /// </summary>
    private void RestoreNapsOnShutdown()
    {
        if (System.Threading.Interlocked.Exchange(ref _napsRestoredOnShutdown, 1) != 0) return;
        try { _taskSleepVm?.RestoreAllNaps(); }
        catch (Exception ex) { Log.Warn("App", $"Restore-on-shutdown failed: {ex.Message}"); }
    }

    // ── Unobserved Task exceptions ──
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        var ex = e.Exception.InnerException ?? e.Exception;
        Log.Error("TaskScheduler", "Unobserved task exception", ex);

        Dispatcher?.BeginInvoke(() =>
        {
            try { CrashReportWindow.ShowError(ex, "Background Task Exception"); }
            catch { /* never throw from exception handler */ }
        });
    }

    private void ShowCrashOnUIThread(Exception? ex, string context)
    {
        // Atomically claim the handler slot — if another call already owns it, bail out.
        // Interlocked.CompareExchange makes the read+set a single atomic operation,
        // preventing the TOCTOU race that volatile bool cannot prevent.
        if (Interlocked.CompareExchange(ref _crashHandlerActiveInt, 1, 0) != 0) return;
        try
        {
            CrashReportWindow.ShowCrash(ex, context);
        }
        catch
        {
            MessageBox.Show(
                $"Systema encountered a fatal unrecoverable error.\n\n{ex?.Message}\n\nContext: {context}",
                "Fatal Error — Systema",
                MessageBoxButton.OK,
                MessageBoxImage.Stop);
            Shutdown(1);
        }
        finally { Interlocked.Exchange(ref _crashHandlerActiveInt, 0); }
    }

    // ── Windows session ending (logoff / shutdown / restart) ──────────────────
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info("App", $"Windows session ending ({e.ReasonSessionEnding}) — disposing resources");
        CrashGuard.Stop();
        _heartbeat?.Dispose();
        _updateService?.StopAutoUpdate();
        _trayService?.Dispose();
        _trayService = null;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CrashGuard.Stop();
        _heartbeat?.Dispose();
        _mainVm?.Dispose();
        _trayService?.Dispose();
        Log.Info("App", $"Systema exiting with code {e.ApplicationExitCode}");
        // Logging is asynchronous now (a background writer thread owns the file), so
        // drain the queue before the process goes away or these final lines are lost.
        Log.Shutdown();
        base.OnExit(e);
    }

    // ── First-run defaults ─────────────────────────────────────────────────────

    private static void ApplyFirstRunDefaults(SettingsService settings)
    {
        const string firstRunKey   = @"SOFTWARE\Systema";
        const string firstRunValue = "FirstRunDefaultsApplied";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(firstRunKey);
            if (key?.GetValue(firstRunValue) != null) return; // already done
        }
        catch { return; }

        try
        {
            // Enable "Start with Windows" so the app runs in the background after reboot.
            if (!settings.StartWithWindows)
                settings.StartWithWindows = true;

            // Mark first-run complete so we never override a user's deliberate choice.
            using var writeKey = Microsoft.Win32.Registry.CurrentUser
                .CreateSubKey(firstRunKey, writable: true);
            writeKey?.SetValue(firstRunValue, 1,
                Microsoft.Win32.RegistryValueKind.DWord);

            Log.Info("App", "First-run defaults applied (StartWithWindows = true)");
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"ApplyFirstRunDefaults failed: {ex.Message}");
        }
    }
}
