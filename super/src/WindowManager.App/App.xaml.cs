using CefSharp;
using CefSharp.Wpf;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using WindowManager.App.Runtime;

namespace WindowManager.App;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private string _watchdogToken = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var startupLogPath = Path.Combine(baseDirectory, "startup.log");

        try
        {
            RegisterGlobalDiagnostics();
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\WindowManagerBroadcast.SingleInstance", createdNew: out var createdNew);
            if (!createdNew)
            {
                File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Secondary instance blocked{Environment.NewLine}");
                Shutdown(0);
                return;
            }

            AppLog.Write("App", "Startup begin");
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Startup begin{Environment.NewLine}");
            _watchdogToken = ParseWatchdogToken(e.Args);
            if (!string.IsNullOrWhiteSpace(_watchdogToken))
            {
                Directory.CreateDirectory(AppDataPaths.WatchdogRoot);
                var exitMarkerPath = AppDataPaths.GetWatchdogExitMarkerPath(_watchdogToken);
                if (File.Exists(exitMarkerPath))
                {
                    File.Delete(exitMarkerPath);
                }
            }

            InitializeBrowserEngine(startupLogPath);

            base.OnStartup(e);
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] After base.OnStartup{Environment.NewLine}");

            var bootstrapper = new Bootstrapper();
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Before CreateMainWindow{Environment.NewLine}");
            var mainWindow = bootstrapper.CreateMainWindow();
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] After CreateMainWindow{Environment.NewLine}");
            MainWindow = mainWindow;
            mainWindow.Show();
            AppLog.Write("App", "MainWindow exibida");
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] After mainWindow.Show{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppLog.Write("App", string.Format("Startup failure: {0}", ex.Message));
            CrashDiagnostics.Report("Startup", ex);
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Startup failure: {ex}{Environment.NewLine}");
            MessageBox.Show(ex.ToString(), "Falha ao iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TryWriteWatchdogExitMarker();

        try
        {
            if (Cef.IsInitialized == true)
            {
                Cef.Shutdown();
            }
        }
        catch
        {
        }

        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
        }
        catch
        {
        }

        base.OnExit(e);
    }

    private void TryWriteWatchdogExitMarker()
    {
        if (string.IsNullOrWhiteSpace(_watchdogToken))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppDataPaths.WatchdogRoot);
            File.WriteAllText(AppDataPaths.GetWatchdogExitMarkerPath(_watchdogToken), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
        }
    }

    private static string ParseWatchdogToken(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return string.Empty;
        }

        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--watchdog-token", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 < args.Length)
            {
                return args[index + 1]?.Trim() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static void InitializeBrowserEngine(string startupLogPath)
    {
        AppRuntimeState.BrowserEngineAvailable = false;
        AppRuntimeState.BrowserEngineStatusMessage = "CEF ainda nao foi inicializado.";

        if (Cef.IsInitialized == true)
        {
            AppRuntimeState.BrowserEngineAvailable = true;
            AppRuntimeState.BrowserEngineStatusMessage = string.Empty;
            return;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var appDataRoot = AppDataPaths.CefRoot;

        Directory.CreateDirectory(appDataRoot);
        Directory.CreateDirectory(Path.Combine(appDataRoot, "cache"));

        var localesDirectory = Path.Combine(baseDirectory, "locales");

        var settings = new CefSettings
        {
            BrowserSubprocessPath = Path.Combine(baseDirectory, "CefSharp.BrowserSubprocess.exe"),
            CachePath = Path.Combine(appDataRoot, "cache"),
            RootCachePath = appDataRoot,
            LogFile = Path.Combine(baseDirectory, "cef.log"),
            LocalesDirPath = Directory.Exists(localesDirectory) ? localesDirectory : baseDirectory,
            ResourcesDirPath = baseDirectory,
            PersistSessionCookies = true,
            WindowlessRenderingEnabled = false
        };

        settings.CefCommandLineArgs["disable-gpu"] = "1";
        settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
        settings.CefCommandLineArgs["disable-features"] = "CalculateNativeWinOcclusion";
        settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
        settings.CefCommandLineArgs["no-proxy-server"] = "1";

        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Before Cef.Initialize{Environment.NewLine}");
        var initialized = Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] After Cef.Initialize => {initialized}{Environment.NewLine}");
        AppLog.Write("CEF", string.Format("Cef.Initialize => {0}", initialized));

        AppRuntimeState.BrowserEngineAvailable = initialized == true;
        AppRuntimeState.BrowserEngineStatusMessage = initialized == true
            ? string.Empty
            : "Falha ao inicializar o CEF nesta maquina.";
    }

    private void RegisterGlobalDiagnostics()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("Crash", string.Format("DispatcherUnhandledException: {0}", e.Exception.Message));
        CrashDiagnostics.Report("DispatcherUnhandledException", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppLog.Write("Crash", string.Format("AppDomainUnhandledException: terminating={0}", e.IsTerminating));
        CrashDiagnostics.Report("AppDomainUnhandledException", exception, string.Format("IsTerminating={0}", e.IsTerminating));
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("Crash", string.Format("TaskSchedulerUnobservedTaskException: {0}", e.Exception.Message));
        CrashDiagnostics.Report("TaskSchedulerUnobservedTaskException", e.Exception);
    }
}
