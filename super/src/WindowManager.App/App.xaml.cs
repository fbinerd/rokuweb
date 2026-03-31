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

    protected override async void OnStartup(StartupEventArgs e)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var startupLogPath = Path.Combine(baseDirectory, "startup.log");
        var toolsDir = Path.Combine(baseDirectory, "tools");

        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] OnStartup BEGIN{Environment.NewLine}");

        SplashScreen splash = new SplashScreen();
        splash.Show();
        splash.SetStatus("Verificando dependências...", 0);
        try
        {
            // 1. Verifica e baixa dependências externas
            await WindowManager.App.Runtime.DependencyChecker.EnsureDependenciesWithProgressAsync(toolsDir, (msg, progress) =>
            {
                splash.Dispatcher.Invoke(() => splash.SetStatus(msg, progress));
            });
            splash.SetStatus("Dependências prontas!", 100);

            // 2. Seleção de canal
            string[] canais = new[] { "stable", "develop", "local" };
            string canalSelecionado = "stable";
            splash.Dispatcher.Invoke(() => {
                var dlg = new Window { Title = "Selecionar Canal", Width = 260, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterScreen, Owner = splash };
                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(10) };
                var combo = new System.Windows.Controls.ComboBox { ItemsSource = canais, SelectedValue = "stable" };
                panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Escolha o canal de atualização:", Margin = new Thickness(0,0,0,8), Foreground = System.Windows.Media.Brushes.White });
                panel.Children.Add(combo);
                var btn = new System.Windows.Controls.Button { Content = "OK", Margin = new Thickness(0,10,0,0), IsDefault = true };
                btn.Click += (_,__) => { canalSelecionado = combo.SelectedValue?.ToString() ?? "stable"; dlg.DialogResult = true; dlg.Close(); };
                panel.Children.Add(btn);
                dlg.Content = panel;
                dlg.ShowDialog();
            });
            splash.SetStatus($"Canal selecionado: {canalSelecionado}", 100);

            // 3. Checagem de update
            splash.SetStatus("Checando atualizações...", 30);
            var manifestService = new AppUpdateManifestService();
            var updateResult = await manifestService.CheckForUpdateAsync(canalSelecionado, CancellationToken.None);
            if (!updateResult.UpdateAvailable)
            {
                splash.SetStatus("Nenhuma atualização disponível.", 100);
            }
            else
            {
                splash.SetStatus($"Update disponível: {updateResult.LatestVersion}", 40);
                // 4. Backup automático
                var maintenanceService = new AppDataMaintenanceService();
                string backupPath = string.Empty;
                try
                {
                    backupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowManagerBroadcast", "backups", $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss}-{updateResult.LatestReleaseId}.zip");
                    await maintenanceService.ExportAsync(backupPath, CancellationToken.None);
                    splash.SetStatus($"Backup criado: {backupPath}", 50);
                }
                catch (Exception ex)
                {
                    splash.SetStatus($"Falha ao criar backup: {ex.Message}", 50);
                }
                // 5. Download e aplicação do update
                splash.SetStatus("Baixando e aplicando update...", 70);
                var selfUpdateService = new AppSelfUpdateService();
                var updateResult2 = await selfUpdateService.DownloadAndPrepareAsync(updateResult, CancellationToken.None);
                if (!updateResult2.Succeeded)
                {
                    splash.SetStatus($"Falha ao aplicar update: {updateResult2.Message}", 100);
                    // 6. Restauração do backup
                    if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                    {
                        splash.SetStatus("Restaurando backup...", 90);
                        await maintenanceService.ImportAsync(backupPath, CancellationToken.None);
                        splash.SetStatus("Backup restaurado.", 100);
                    }
                    MessageBox.Show($"Falha ao aplicar update: {updateResult2.Message}\nBackup restaurado.", "Erro de Update", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(-1);
                    return;
                }
                splash.SetStatus("Update aplicado com sucesso!", 100);
            }

            RegisterGlobalDiagnostics();
            _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Local\WindowManagerBroadcast.SingleInstance", createdNew: out var createdNew);
            if (!createdNew)
            {
                File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Secondary instance blocked{Environment.NewLine}");
                splash.Close();
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

            var restoreBackupPath = ParseRestoreBackupPath(e.Args);
            if (!string.IsNullOrWhiteSpace(restoreBackupPath) && File.Exists(restoreBackupPath))
            {
                AppLog.Write("Updater", string.Format("Restaurando backup solicitado na linha de comando: {0}", restoreBackupPath));
                var maintenanceService = new AppDataMaintenanceService();
                maintenanceService.ImportAsync(restoreBackupPath, CancellationToken.None).GetAwaiter().GetResult();
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
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] After mainWindow.Show{Environment.NewLine}");
            AppLog.Write("App", "MainWindow exibida");
        }
        catch (Exception ex)
        {
            AppLog.Write("App", string.Format("Startup failure: {0}", ex.Message));
            CrashDiagnostics.Report("Startup", ex);
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Startup failure: {ex}{Environment.NewLine}");
            MessageBox.Show(ex.ToString(), "Falha ao iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            splash.Close();
        }
        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] OnStartup END{Environment.NewLine}");
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

    private static string ParseRestoreBackupPath(string[] args)
    {
        if (args is null || args.Length == 0)
        {
            return string.Empty;
        }

        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], "--restore-backup", StringComparison.OrdinalIgnoreCase))
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
