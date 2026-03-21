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
    private TrayService?   _trayService;
    private MainWindow?    _mainWindow;
    private MainViewModel? _mainVm;
    private UpdateService? _updateService;

    // Single-instance guard — prevents the watchdog task from spawning duplicates
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Wire all global exception handlers before anything else ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        // Keep the app alive even when no window is visible (tray mode)
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Log.Info("App", "Starting Systema...");

        // ── Single-instance guard ──
        // If the watchdog task (or user) launches a second instance while Systema
        // is already running, this mutex catches it and exits immediately.
        _singleInstanceMutex = new Mutex(true, "Global\\SystemaSingleInstance", out bool isNewInstance);
        if (!isNewInstance)
        {
            Log.Info("App", "Another instance is already running — exiting");
            _singleInstanceMutex.Dispose();
            Shutdown(0);
            return;
        }

        // ── Check for crash from previous session ──
        // CrashGuard writes a sentinel file before risky operations and deletes it
        // when they complete. If it still exists → previous session crashed mid-operation.
        var previousCrash = CrashGuard.CheckPreviousCrash();
        if (previousCrash != null)
        {
            Log.Warn("App", "Previous session crash detected — showing report");
            CrashReportWindow.ShowPreviousCrash(previousCrash);
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
            var gameboosterService  = new GameBoosterService(serviceControl, settingsService, processLassoService);
            var realtekService      = new RealtekCleanerService();
            var coreParkingService  = new CoreParkingService();
            var wuTweaksService     = new WindowsUpdateTweaksService();
            var stabilityService    = new SystemStabilityService();
            var bloatwareService    = new BloatwareService();
            _updateService          = new UpdateService(settingsService);
            var watchdogService     = new WatchdogService();
            var healthService       = new HealthScoreService(
                memoryService, startupService, telemetryService,
                animationService, powerPlanService);

            Log.Info("App", "All services instantiated");

            // ── First-run defaults ──
            // Enable "Start with Windows" automatically on first launch so the app
            // is available in the background without the user having to opt-in.
            ApplyFirstRunDefaults(settingsService);

            // TaskSleepViewModel must be created before DashboardViewModel (dashboard reads its live process list)
            var taskSleepVm   = new TaskSleepViewModel();

            var dashboardVm   = new DashboardViewModel(
                gameboosterService, taskSleepVm, serviceControl,
                memoryService, dnsService, powerPlanService,
                wuTweaksService, coreParkingService, settingsService, optFeatures);

            var memoryVm      = new MemoryViewModel(memoryService, startupService);
            var servicesVm    = new ServicesViewModel(serviceControl, optFeatures, restoreService, settingsService);
            var visualVm      = new VisualViewModel(animationService, powerPlanService, settingsService);
            var gameBoosterVm = new GameBoosterViewModel(gameboosterService, settingsService);
            var settingsVm    = new SettingsViewModel(settingsService, restoreService, _updateService, watchdogService);
            var toolsVm       = new ToolsViewModel(
                realtekService, coreParkingService, restoreService,
                settingsService, dnsService, wuTweaksService, stabilityService);
            var bloatwareVm   = new BloatwareViewModel(bloatwareService, restoreService, settingsService);

            _mainVm = new MainViewModel(dashboardVm, memoryVm, servicesVm,
                                        visualVm, gameBoosterVm, settingsVm, toolsVm, taskSleepVm, bloatwareVm);

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

            // Start background game monitoring (passes tray ref for balloon notifications)
            gameboosterService.StartMonitoring(_trayService);

            // ── Auto-updater ──
            // Starts the background loop: checks on startup (20 s delay), re-checks
            // every 2 days, and installs silently when CPU has been idle for 5 minutes.
            // ShutdownRequested fires just before the installer launches.
            _updateService.ShutdownRequested += () =>
                Dispatcher.Invoke(() =>
                {
                    Log.Info("App", "Auto-updater requesting shutdown to apply update");
                    CrashGuard.Stop();
                    _trayService?.Dispose();
                    Shutdown(0);
                });
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
        }
        catch (Exception ex)
        {
            Log.Fatal("App", "Startup composition failed", ex);
            ShowCrashOnUIThread(ex, "Application Startup");
        }
    }

    // ── Show / Hide main window ────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow(_mainVm!);
            // NOTE: We never let the window truly close — Close button & Minimize both
            // call Hide(). The window is fully destroyed only on ExplicitShutdown().
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
        _trayService?.Dispose();
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
        bool isXamlError = e.Exception is System.Windows.Markup.XamlParseException
            || e.Exception.InnerException is System.Windows.Markup.XamlParseException
            || e.Exception is InvalidOperationException
               && (e.Exception.Message.Contains("TargetType") || e.Exception.Message.Contains("Style"));

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

        if (Dispatcher != null && !Dispatcher.CheckAccess())
            Dispatcher.Invoke(() => ShowCrashOnUIThread(ex, "Background Thread Exception"));
        else
            ShowCrashOnUIThread(ex, "Background Thread Exception");
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
        _updateService?.StopAutoUpdate();
        _trayService?.Dispose();
        _trayService = null;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CrashGuard.Stop();
        _trayService?.Dispose();
        Log.Info("App", $"Systema exiting with code {e.ApplicationExitCode}");
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
