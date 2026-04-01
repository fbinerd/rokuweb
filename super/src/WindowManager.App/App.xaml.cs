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


        SplashScreen splash = null;
        TaskCompletionSource<bool> updateDone;
        try
        {
            splash = new SplashScreen();
            splash.Show();
            splash.ShowProgressBar(false);
            updateDone = new TaskCompletionSource<bool>();
            // Lê config do usuário
            string configPath = Path.Combine(baseDirectory, "user.config.json");
            string defaultBranch = "stable";
            bool autoUpdate = false;
            bool setDefaultBranch = false;
            try
            {
                if (File.Exists(configPath))
                {
                    var configJson = File.ReadAllText(configPath);
                    var config = Newtonsoft.Json.Linq.JObject.Parse(configJson);
                    defaultBranch = config["DefaultBranch"]?.ToString() ?? "stable";
                    autoUpdate = config["AutoUpdate"]?.ToObject<bool?>() ?? false;
                    setDefaultBranch = config["SetDefaultBranch"]?.ToObject<bool?>() ?? false;
                }
            }
            catch { }

            // Exibe versões antes de qualquer download
            splash.SetInstalledVersion(WindowManager.App.Runtime.BuildVersionInfo.Version + " (" + WindowManager.App.Runtime.BuildVersionInfo.ReleaseId + ")");
            string[] canais = new[] { "stable", "develop", "local" };
            string canalSelecionado = defaultBranch;
            bool autoUpdateChecked = autoUpdate;
            var manifestService = new AppUpdateManifestService();
            // Função para atualizar a versão remota (com número da build)
            async Task UpdateRemoteVersion(string branch)
            {
                splash.SetRemoteVersion("Buscando...");
                try {
                    var result = await manifestService.CheckForUpdateAsync(branch, CancellationToken.None);
                    if (!string.IsNullOrEmpty(result.LatestVersion) && !string.IsNullOrEmpty(result.LatestReleaseId))
                        splash.SetRemoteVersion($"{result.LatestVersion} (build {result.LatestReleaseId})");
                    else if (!string.IsNullOrEmpty(result.LatestVersion))
                        splash.SetRemoteVersion(result.LatestVersion);
                    else
                        splash.SetRemoteVersion("-");
                } catch (Exception ex) {
                    splash.SetRemoteVersion("Erro ao buscar versão");
                }
            }
            // Garantir que updateDone só seja declarado uma vez
            // (Removido: já foi declarado anteriormente)
            splash.Dispatcher.Invoke(() => {
                splash.SetChannels(canais, canalSelecionado);
                splash.SetAutoUpdateChecked(autoUpdateChecked);
                splash.SetSetDefaultBranchChecked(setDefaultBranch);
                // Registra o handler do botão OK imediatamente
                splash.GetOkButton().Click += async (_,__) => {
                    try {
                        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] OK Clicked\n");
                        canalSelecionado = splash.SelectedChannel;
                        autoUpdateChecked = splash.IsAutoUpdateChecked;
                        setDefaultBranch = splash.IsSetDefaultBranchChecked;
                        if (autoUpdateChecked || setDefaultBranch)
                        {
                            File.WriteAllText(configPath, Newtonsoft.Json.JsonConvert.SerializeObject(new { DefaultBranch = canalSelecionado, AutoUpdate = autoUpdateChecked, SetDefaultBranch = setDefaultBranch }, Newtonsoft.Json.Formatting.Indented));
                        }
                        var ok = await RunUpdateAndTools(canalSelecionado);
                        updateDone.SetResult(ok);
                    } catch (Exception ex) {
                        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Exception in OK handler: {ex}\n");
                        MessageBox.Show($"Erro inesperado: {ex}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        updateDone.SetResult(false);
                    }
                };
            });
            // Atualiza a versão remota ao abrir
            await UpdateRemoteVersion(canalSelecionado);
            // Atualiza a versão remota ao trocar canal
            splash.Dispatcher.Invoke(() => {
                splash.ChannelCombo.SelectionChanged += async (s, e) => {
                    var branch = splash.SelectedChannel;
                    await UpdateRemoteVersion(branch);
                };
            });
            // Função para executar o fluxo de update/tools
            async Task<bool> RunUpdateAndTools(string branch)
            {
                try {
                    splash.ShowProgressBar(true);
                    splash.SetStatus("Baixando e aplicando update...", 10);
                    var updateResult = await manifestService.CheckForUpdateAsync(branch, CancellationToken.None);
                    if (!string.IsNullOrEmpty(updateResult.LatestVersion) && !string.IsNullOrEmpty(updateResult.LatestReleaseId))
                        splash.SetRemoteVersion($"{updateResult.LatestVersion} (build {updateResult.LatestReleaseId})");
                    else if (!string.IsNullOrEmpty(updateResult.LatestVersion))
                        splash.SetRemoteVersion(updateResult.LatestVersion);
                    else
                        splash.SetRemoteVersion("-");
                    if (!updateResult.UpdateAvailable)
                    {
                        splash.SetStatus("Nenhuma atualização disponível.", 100);
                        splash.DialogResult = true;
                        return true;
                    }
                    splash.SetStatus($"Update disponível: {updateResult.LatestVersion} (build {updateResult.LatestReleaseId})", 30);
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
                    splash.SetStatus("Baixando e aplicando update...", 70);
                    var selfUpdateService = new AppSelfUpdateService();
                    var updateResult2 = await selfUpdateService.DownloadAndPrepareAsync(updateResult, CancellationToken.None);
                    if (!updateResult2.Succeeded)
                    {
                        splash.SetStatus($"Falha ao aplicar update: {updateResult2.Message}", 100);
                        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                        {
                            splash.SetStatus("Restaurando backup...", 90);
                            await maintenanceService.ImportAsync(backupPath, CancellationToken.None);
                            splash.SetStatus("Backup restaurado.", 100);
                        }
                        MessageBox.Show($"Falha ao aplicar update: {updateResult2.Message}\nBackup restaurado.", "Erro de Update", MessageBoxButton.OK, MessageBoxImage.Error);
                        Shutdown(-1);
                        return false;
                    }
                    splash.SetStatus("Update aplicado com sucesso!", 100);
                    // Só agora baixa/configura tools
                    splash.SetStatus("Baixando e configurando ferramentas...", 90);
                    await WindowManager.App.Runtime.DependencyChecker.EnsureDependenciesWithProgressAsync(toolsDir, (msg, progress) =>
                    {
                        splash.Dispatcher.Invoke(() => splash.SetStatus(msg, progress));
                    });
                    splash.SetStatus("Dependências prontas!", 100);
                    splash.DialogResult = true;
                    return true;
                } catch (Exception ex) {
                    File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Exception in RunUpdateAndTools: {ex}\n");
                    MessageBox.Show($"Erro inesperado: {ex}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }
            }
            // Removido: updateDone já foi declarado e inicializado acima
            // Se autoUpdate estiver marcado, já inicia o fluxo automaticamente
            if (autoUpdateChecked)
            {
                _ = Task.Run(async () => {
                    File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] AutoUpdate iniciado automaticamente\n");
                    var ok = await RunUpdateAndTools(canalSelecionado);
                    updateDone.SetResult(ok);
                });
            }
            // Espera o usuário clicar em OK ou terminar o processo automático
            await updateDone.Task;
            // Fecha a splash antes de criar a janela principal
            if (splash != null)
            {
                splash.Dispatcher.Invoke(() => splash.Close());
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
