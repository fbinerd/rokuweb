using CefSharp;
using CefSharp.Wpf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WindowManager.App.Profiles;
using WindowManager.App.Runtime;
using WindowManager.App.ViewModels;

namespace WindowManager.App;

public partial class App : Application
{
    private const string SkipUpdaterArgument = "--skip-updater";
    private const string LaunchedByLauncherArgument = "--launched-by-launcher";
    private static Mutex? _singleInstanceMutex;
    private string _watchdogToken = string.Empty;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var previousShutdownMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [GLOBAL] UnhandledException: {ex.ExceptionObject}\n");
        };

        DispatcherUnhandledException += (s, ex) =>
        {
            File.AppendAllText("startup.log", $"[{DateTime.Now:O}] [GLOBAL] DispatcherUnhandledException: {ex.Exception}\n");
            ex.Handled = true;
        };

        var baseDirectory = AppContext.BaseDirectory;
        var startupLogPath = Path.Combine(baseDirectory, "startup.log");
        var toolsDir = Path.Combine(baseDirectory, "tools");
        var forcedLogPath = "C:/Users/user/Documents/app/rokuweb/startup.log";
        var profileStore = new ProfileStore();
        var preferenceStore = new AppUpdatePreferenceStore();

        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] OnStartup BEGIN{Environment.NewLine}");
        File.AppendAllText(forcedLogPath, $"[{DateTime.Now:O}] OnStartup BEGIN{Environment.NewLine}");

        void LogStep(string message)
        {
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            File.AppendAllText(forcedLogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }

        SplashScreen? splash = null;

        try
        {
            LogStep("OnStartup inicializando updater");
            splash = new SplashScreen();
            var skipUpdater = HasArgument(e.Args, SkipUpdaterArgument);
            var launchedByLauncher = HasArgument(e.Args, LaunchedByLauncherArgument);

            if (!launchedByLauncher)
            {
                LogStep("Inicializacao bloqueada: SuperPainel foi aberto sem passar pelo launcher.");
                MessageBox.Show(
                    "Abra o Super pelo SuperLauncher. A abertura direta do SuperPainel foi bloqueada.",
                    "Inicialização bloqueada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(-1);
                return;
            }

            var startupProfileName = await profileStore.GetStartupProfileNameAsync(CancellationToken.None);
            var startupProfile = await profileStore.LoadAsync(startupProfileName, CancellationToken.None);
            var startupPreferences = await preferenceStore.LoadAsync(CancellationToken.None);
            var startupSettings = BuildStartupUpdateSettings(startupProfile, startupPreferences);
            LogStep(
                $"Startup settings: profile={startupProfileName}, branch={startupSettings.Channel}, " +
                $"auto={startupSettings.AutoUpdateEnabled}, default={startupSettings.RememberDefaultBranch}");

            var manifestService = new AppUpdateManifestService();
            var channels = new[] { UpdateChannelNames.Stable, UpdateChannelNames.Develop, UpdateChannelNames.Local };
            var prefetchedUpdates = skipUpdater
                ? null
                : await PrefetchUpdateInfoAsync(manifestService, channels, LogStep);

            if (skipUpdater)
            {
                LogStep("Launcher solicitou pular updater no startup.");
            }
            else if (prefetchedUpdates is not null)
            {
                var initialAutoStart = startupSettings.RememberDefaultBranch && startupSettings.AutoUpdateEnabled;
                splash.SetInstalledVersion(FormatInstalledVersion(startupSettings.Channel));
                splash.SetChannels(channels, startupSettings.Channel);
                splash.SetSetDefaultBranchChecked(startupSettings.RememberDefaultBranch);
                splash.SetAutoUpdateChecked(startupSettings.AutoUpdateEnabled && startupSettings.RememberDefaultBranch);
                splash.SetCompactMode(initialAutoStart);
                splash.ShowProgressBar(false);
                splash.Show();

                await UpdateRemoteVersionAsync(
                    splash,
                    startupSettings.Channel,
                    manifestService,
                    prefetchedUpdates,
                    LogStep);

                splash.ChannelCombo.SelectionChanged += async (_, __) =>
                {
                    await UpdateRemoteVersionAsync(
                        splash,
                        splash.SelectedChannel,
                        manifestService,
                        prefetchedUpdates,
                        LogStep);
                };

                var selectedChannel = splash.SelectedChannel;
                var autoUpdateChecked = splash.IsAutoUpdateChecked;
                var rememberDefaultBranch = splash.IsSetDefaultBranchChecked;

                async Task SaveSettingsAsync()
                {
                    selectedChannel = UpdateChannelNames.Normalize(splash.SelectedChannel);
                    autoUpdateChecked = splash.IsAutoUpdateChecked;
                    rememberDefaultBranch = splash.IsSetDefaultBranchChecked;

                    var effectiveAutoUpdate = autoUpdateChecked && rememberDefaultBranch;
                    var profileToSave = startupProfile ?? new AppProfile { Name = startupProfileName };
                    profileToSave.UpdateChannel = selectedChannel;
                    profileToSave.AutoUpdateEnabled = effectiveAutoUpdate;
                    profileToSave.RememberUpdateChannelSelection = rememberDefaultBranch;
                    await profileStore.SaveAsync(profileToSave, CancellationToken.None);

                    startupProfile = profileToSave;

                    await preferenceStore.SaveAsync(
                        new AppUpdatePreferences
                        {
                            AutoUpdateEnabled = effectiveAutoUpdate,
                            UpdateChannel = selectedChannel,
                            RememberUpdateChannelSelection = rememberDefaultBranch,
                            AdditionalDiscoveryCidrs = startupPreferences.AdditionalDiscoveryCidrs
                        },
                        CancellationToken.None);
                }

                async Task<bool> RunUpdateAndDependenciesAsync(string branch)
                {
                    try
                    {
                        void RunOnSplashUi(Action action)
                        {
                            if (splash.Dispatcher.CheckAccess())
                            {
                                action();
                                return;
                            }

                            splash.Dispatcher.Invoke(action);
                        }

                        static (string Status, string Details, bool PreserveDetails) SplitProgressMessage(string message)
                        {
                            if (string.IsNullOrWhiteSpace(message))
                            {
                                return (string.Empty, string.Empty, false);
                            }

                            if (message.StartsWith("Baixando pacotes ", StringComparison.OrdinalIgnoreCase))
                            {
                                var markerIndex = message.IndexOf("...", StringComparison.Ordinal);
                                if (markerIndex >= 0)
                                {
                                    var status = message.Substring(0, markerIndex + 3);
                                    var details = message.Substring(markerIndex + 3).Trim();
                                    return string.IsNullOrWhiteSpace(details)
                                        ? (status, string.Empty, true)
                                        : (status, details, false);
                                }
                            }

                            return (message, string.Empty, false);
                        }

                        await splash.Dispatcher.InvokeAsync(() =>
                        {
                            splash.ShowProgressBar(true);
                            splash.SetStatus("Iniciando atualização...", 1);
                            splash.SetProgressDetails(string.Empty);
                            splash.GetOkButton().IsEnabled = false;
                            splash.ChannelCombo.IsEnabled = false;
                        }, DispatcherPriority.Render);

                        await Task.Yield();

                        double currentProgress = 0;
                        string currentProgressDetails = string.Empty;
                        void SetProgress(string message, double progress)
                        {
                            currentProgress = Math.Max(currentProgress, Math.Max(0, Math.Min(100, progress)));
                            var (status, details, preserveDetails) = SplitProgressMessage(message);
                            if (!string.IsNullOrWhiteSpace(details))
                            {
                                currentProgressDetails = details;
                            }
                            else if (!preserveDetails)
                            {
                                currentProgressDetails = string.Empty;
                            }

                            RunOnSplashUi(() =>
                            {
                                splash.SetStatus(status, currentProgress);
                                splash.SetProgressDetails(currentProgressDetails);
                            });
                        }

                        double MapProgress(double phaseStart, double phaseEnd, double phaseProgress)
                        {
                            var normalized = Math.Max(0, Math.Min(100, phaseProgress)) / 100.0;
                            return phaseStart + ((phaseEnd - phaseStart) * normalized);
                        }

                        static int GetUpdateDownloadItemCount(AppUpdateCheckResult updateResult)
                        {
                            if (updateResult is null || !updateResult.UpdateAvailable)
                            {
                                return 0;
                            }

                            var packageUrls = updateResult.PackageUrls ?? Array.Empty<string>();
                            if (packageUrls.Length > 0)
                            {
                                return packageUrls.Length;
                            }

                            return string.IsNullOrWhiteSpace(updateResult.RecommendedPackageUrl) ? 0 : 1;
                        }

                        static string RemapDownloadMessage(string message, int downloadOffset, int totalDownloadItems)
                        {
                            if (string.IsNullOrWhiteSpace(message) || totalDownloadItems <= 0)
                            {
                                return message;
                            }

                            const string prefix = "Baixando pacotes ";
                            if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                return message;
                            }

                            var numberStart = prefix.Length;
                            var slashIndex = message.IndexOf('/', numberStart);
                            var ellipsisIndex = message.IndexOf("...", StringComparison.Ordinal);
                            if (slashIndex <= numberStart || ellipsisIndex <= slashIndex)
                            {
                                return message;
                            }

                            if (!int.TryParse(message.Substring(numberStart, slashIndex - numberStart), out var localCurrent))
                            {
                                return message;
                            }

                            var globalCurrent = downloadOffset + localCurrent;
                            var suffix = message.Substring(ellipsisIndex + 3).Trim();
                            var status = $"Baixando pacotes {globalCurrent}/{totalDownloadItems}...";
                            return string.IsNullOrWhiteSpace(suffix)
                                ? status
                                : $"{status} {suffix}";
                        }

                        SetProgress("Verificando atualizações...", 5);

                        var normalizedBranch = UpdateChannelNames.Normalize(branch);
                        var updateResult = prefetchedUpdates.TryGetValue(normalizedBranch, out var cached)
                            ? cached
                            : await manifestService.CheckForUpdateAsync(normalizedBranch, CancellationToken.None);

                        RunOnSplashUi(() =>
                        {
                            splash.SetInstalledVersion(FormatInstalledVersion(normalizedBranch));
                            splash.SetRemoteVersion(FormatRemoteVersion(normalizedBranch, updateResult));
                        });

                        var dependencyPlan = WindowManager.App.Runtime.DependencyChecker.PlanMissingDependencies(toolsDir);
                        var hasDependencyDownloads = dependencyPlan.HasPendingDownloads;
                        var updateDownloadItemCount = GetUpdateDownloadItemCount(updateResult);
                        var totalDownloadItems = updateDownloadItemCount + dependencyPlan.Items.Length;

                        if (updateResult.UpdateAvailable)
                        {
                            SetProgress(
                                $"Atualização disponível: {updateResult.LatestVersion} (build {updateResult.LatestReleaseId})",
                                12);

                            var maintenanceService = new AppDataMaintenanceService();
                            var backupPath = string.Empty;

                            try
                            {
                                backupPath = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "WindowManagerBroadcast",
                                    "backups",
                                    $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss}-{updateResult.LatestReleaseId}.zip");
                                SetProgress("Criando backup antes da atualização...", 18);
                                await maintenanceService.ExportAsync(
                                    backupPath,
                                    (message, progress) => SetProgress(message, MapProgress(18, 25, progress)),
                                    CancellationToken.None);
                                SetProgress($"Backup criado: {backupPath}", 25);
                            }
                            catch (Exception ex)
                            {
                                SetProgress($"Falha ao criar backup: {ex.Message}", 25);
                            }

                            var selfUpdateService = new AppSelfUpdateService();
                            AppSelfUpdateDownloadResult? updateDownload = null;

                            if (hasDependencyDownloads)
                            {
                                SetProgress("Baixando atualização...", 30);
                                updateDownload = await selfUpdateService.DownloadPackagesAsync(
                                    updateResult,
                                    (message, progress) => SetProgress(RemapDownloadMessage(message, 0, totalDownloadItems), MapProgress(30, 55, progress)),
                                    CancellationToken.None);

                                if (!updateDownload.Succeeded)
                                {
                                    SetProgress($"Falha ao baixar atualização: {updateDownload.Message}", 100);
                                    return false;
                                }

                                SetProgress("Baixando dependências...", 55);
                                await WindowManager.App.Runtime.DependencyChecker.DownloadDependenciesAsync(
                                    dependencyPlan,
                                    (message, progress) => SetProgress(RemapDownloadMessage(message, updateDownloadItemCount, totalDownloadItems), MapProgress(55, 75, progress)),
                                    CancellationToken.None);
                            }
                            else
                            {
                                SetProgress("Baixando atualização...", 30);
                                updateDownload = await selfUpdateService.DownloadPackagesAsync(
                                    updateResult,
                                    (message, progress) => SetProgress(RemapDownloadMessage(message, 0, totalDownloadItems), MapProgress(30, 75, progress)),
                                    CancellationToken.None);

                                if (!updateDownload.Succeeded)
                                {
                                    SetProgress($"Falha ao baixar atualização: {updateDownload.Message}", 100);
                                    return false;
                                }
                            }

                            SetProgress("Aplicando atualização...", hasDependencyDownloads ? 75 : 80);
                            var applyResult = await selfUpdateService.PrepareDownloadedPackagesAsync(
                                updateResult,
                                updateDownload!,
                                (message, progress) => SetProgress(message, MapProgress(hasDependencyDownloads ? 75 : 80, hasDependencyDownloads ? 88 : 95, progress)),
                                CancellationToken.None);

                            if (!applyResult.Succeeded)
                            {
                                SetProgress($"Falha ao aplicar atualização: {applyResult.Message}", 100);

                                if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                                {
                                    SetProgress("Restaurando backup...", 90);
                                    await maintenanceService.ImportAsync(backupPath, CancellationToken.None);
                                    SetProgress("Backup restaurado.", 100);
                                }

                                MessageBox.Show(
                                    $"Falha ao aplicar atualização: {applyResult.Message}\nBackup restaurado.",
                                    "Erro de Atualização",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error);
                                Shutdown(-1);
                                return false;
                            }

                            SetProgress("Atualização aplicada com sucesso!", hasDependencyDownloads ? 88 : 95);
                        }
                        else
                        {
                            SetProgress("Nenhuma atualização disponível.", 30);
                        }

                        if (hasDependencyDownloads)
                        {
                            if (updateResult.UpdateAvailable)
                            {
                                SetProgress("Instalando dependências baixadas...", 88);
                                await WindowManager.App.Runtime.DependencyChecker.InstallDownloadedDependenciesAsync(
                                    dependencyPlan,
                                    (message, progress) => SetProgress(message, MapProgress(88, 100, progress)),
                                    CancellationToken.None);
                            }
                            else
                            {
                                SetProgress("Baixando dependências...", 30);
                                await WindowManager.App.Runtime.DependencyChecker.DownloadDependenciesAsync(
                                    dependencyPlan,
                                    (message, progress) => SetProgress(RemapDownloadMessage(message, 0, totalDownloadItems), MapProgress(30, 75, progress)),
                                    CancellationToken.None);
                                SetProgress("Instalando dependências baixadas...", 75);
                                await WindowManager.App.Runtime.DependencyChecker.InstallDownloadedDependenciesAsync(
                                    dependencyPlan,
                                    (message, progress) => SetProgress(message, MapProgress(75, 100, progress)),
                                    CancellationToken.None);
                            }
                            SetProgress("Dependências prontas!", 100);
                        }
                        else
                        {
                            SetProgress("Verificando e configurando dependências...", updateResult.UpdateAvailable ? 95 : 80);
                            await WindowManager.App.Runtime.DependencyChecker.EnsureDependenciesWithProgressAsync(
                                toolsDir,
                                (message, progress) => SetProgress(message, MapProgress(updateResult.UpdateAvailable ? 95 : 80, 100, progress)));
                            SetProgress("Dependências prontas!", 100);
                        }
                        RunOnSplashUi(() => splash.Close());
                        return true;
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Exception in RunUpdateAndDependenciesAsync: {ex}\n");
                        MessageBox.Show($"Erro inesperado: {ex}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                }

                var shouldAutoStart = rememberDefaultBranch && autoUpdateChecked;

                LogStep(
                    $"Updater splash pronta: branch={selectedChannel}, auto={autoUpdateChecked}, " +
                    $"default={rememberDefaultBranch}, autoStart={shouldAutoStart}");

                if (shouldAutoStart)
                {
                    await SaveSettingsAsync();
                    var autoUpdateOk = await RunUpdateAndDependenciesAsync(selectedChannel);
                    if (!autoUpdateOk)
                    {
                        return;
                    }
                }
                else
                {
                    var confirmation = new TaskCompletionSource<bool>();

                    splash.GetOkButton().Click += async (_, __) =>
                    {
                        try
                        {
                            await SaveSettingsAsync();
                            var ok = await RunUpdateAndDependenciesAsync(splash.SelectedChannel);
                            confirmation.TrySetResult(ok);
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Exception in OK handler: {ex}\n");
                            MessageBox.Show($"Erro inesperado: {ex}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                            confirmation.TrySetResult(false);
                        }
                    };

                    var confirmed = await confirmation.Task;
                    if (!confirmed)
                    {
                        return;
                    }
                }
            }
            else if (!skipUpdater)
            {
                LogStep("Prefetch falhou ou excedeu timeout; abrindo app sem updater");
            }

            if (splash is not null && splash.IsLoaded)
            {
                splash.Close();
            }

            RegisterGlobalDiagnostics();
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: @"Local\WindowManagerBroadcast.SingleInstance",
                createdNew: out var createdNew);

            if (!createdNew)
            {
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
                AppLog.Write("Updater", $"Restaurando backup solicitado na linha de comando: {restoreBackupPath}");
                var maintenanceService = new AppDataMaintenanceService();
                maintenanceService.ImportAsync(restoreBackupPath, CancellationToken.None).GetAwaiter().GetResult();
            }

            InitializeBrowserEngine(startupLogPath);
            base.OnStartup(e);

            var bootstrapper = new Bootstrapper();
            var mainWindow = bootstrapper.CreateMainWindow();
            MainWindow = mainWindow;

            if (mainWindow.DataContext is MainViewModel startupViewModel)
            {
                await startupViewModel.InitializeAfterStartupAsync();
            }

            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            AppLog.Write("App", "MainWindow exibida");
        }
        catch (Exception ex)
        {
            LogStep($"[CATCH] Excecao capturada apos await: {ex}");
            AppLog.Write("App", $"Startup failure: {ex.Message}");
            CrashDiagnostics.Report("Startup", ex);
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] Startup failure: {ex}{Environment.NewLine}");
            MessageBox.Show(ex.ToString(), "Falha ao iniciar", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            LogStep("[FINALLY] Entrou no finally do OnStartup");

            if (splash is not null && splash.IsLoaded)
            {
                try
                {
                    splash.Close();
                    LogStep("[FINALLY] splash.Close() chamado");
                }
                catch (Exception ex)
                {
                    LogStep($"[FINALLY] Falha ao fechar splash: {ex}");
                }
            }

            if (MainWindow is null)
            {
                ShutdownMode = previousShutdownMode;
            }
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

    private static StartupUpdateSettings BuildStartupUpdateSettings(AppProfile? profile, AppUpdatePreferences preferences)
    {
        var channel = UpdateChannelNames.Normalize(preferences.UpdateChannel);
        var autoUpdate = preferences.AutoUpdateEnabled;
        var rememberDefaultBranch = preferences.RememberUpdateChannelSelection;

        if (profile is not null)
        {
            if (string.IsNullOrWhiteSpace(preferences.UpdateChannel))
            {
                channel = UpdateChannelNames.Normalize(profile.UpdateChannel);
            }

            if (!preferences.AutoUpdateEnabled && profile.AutoUpdateEnabled)
            {
                autoUpdate = profile.AutoUpdateEnabled;
            }

            if (!preferences.RememberUpdateChannelSelection && profile.RememberUpdateChannelSelection)
            {
                rememberDefaultBranch = profile.RememberUpdateChannelSelection;
            }
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = UpdateChannelNames.Stable;
        }

        return new StartupUpdateSettings(
            channel,
            autoUpdate && rememberDefaultBranch,
            rememberDefaultBranch);
    }

    private static async Task<Dictionary<string, AppUpdateCheckResult>?> PrefetchUpdateInfoAsync(
        AppUpdateManifestService manifestService,
        IReadOnlyList<string> channels,
        Action<string> logStep)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var results = new Dictionary<string, AppUpdateCheckResult>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var channel in channels)
            {
                results[channel] = await manifestService.CheckForUpdateAsync(channel, timeoutCts.Token);
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            logStep("Prefetch de updates excedeu o timeout. Abrindo o app sem atualizador.");
            return null;
        }
        catch (Exception ex)
        {
            logStep($"Falha ao buscar versoes remotas antes do splash: {ex}");
            return null;
        }
    }

    private static async Task UpdateRemoteVersionAsync(
        SplashScreen splash,
        string branch,
        AppUpdateManifestService manifestService,
        IReadOnlyDictionary<string, AppUpdateCheckResult> prefetchedUpdates,
        Action<string> logStep)
    {
        var normalizedBranch = UpdateChannelNames.Normalize(branch);

        if (prefetchedUpdates.TryGetValue(normalizedBranch, out var cachedResult))
        {
            splash.SetRemoteVersion(FormatRemoteVersion(normalizedBranch, cachedResult));
            return;
        }

        splash.SetRemoteVersion($"branch {normalizedBranch}: buscando...");

        try
        {
            var result = await manifestService.CheckForUpdateAsync(normalizedBranch, CancellationToken.None);
            splash.SetRemoteVersion(FormatRemoteVersion(normalizedBranch, result));
        }
        catch (Exception ex)
        {
            logStep($"Falha ao atualizar versao remota do canal {normalizedBranch}: {ex}");
            splash.SetRemoteVersion($"branch {normalizedBranch}: erro ao buscar versao");
        }
    }

    private static string FormatInstalledVersion(string branch)
    {
        var normalizedBranch = UpdateChannelNames.Normalize(branch);
        return $"branch {normalizedBranch}: {BuildVersionInfo.Version} ({BuildVersionInfo.ReleaseId})";
    }

    private static string FormatRemoteVersion(string branch, AppUpdateCheckResult? result)
    {
        var normalizedBranch = UpdateChannelNames.Normalize(branch);
        if (result is null)
        {
            return $"branch {normalizedBranch}: -";
        }

        if (!string.IsNullOrEmpty(result.LatestVersion) && !string.IsNullOrEmpty(result.LatestReleaseId))
        {
            return $"branch {normalizedBranch}: {result.LatestVersion} (build {result.LatestReleaseId})";
        }

        return !string.IsNullOrEmpty(result.LatestVersion)
            ? $"branch {normalizedBranch}: {result.LatestVersion}"
            : $"branch {normalizedBranch}: -";
    }

    private static bool HasArgument(string[] args, string expected)
    {
        if (args is null || args.Length == 0 || string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        foreach (var arg in args)
        {
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
        AppLog.Write("CEF", $"Cef.Initialize => {initialized}");

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
        AppLog.Write("Crash", $"DispatcherUnhandledException: {e.Exception.Message}");
        CrashDiagnostics.Report("DispatcherUnhandledException", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        AppLog.Write("Crash", $"AppDomainUnhandledException: terminating={e.IsTerminating}");
        CrashDiagnostics.Report("AppDomainUnhandledException", exception, $"IsTerminating={e.IsTerminating}");
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("Crash", $"TaskSchedulerUnobservedTaskException: {e.Exception.Message}");
        CrashDiagnostics.Report("TaskSchedulerUnobservedTaskException", e.Exception);
    }

    private sealed class StartupUpdateSettings
    {
        public StartupUpdateSettings(string channel, bool autoUpdateEnabled, bool rememberDefaultBranch)
        {
            Channel = channel;
            AutoUpdateEnabled = autoUpdateEnabled;
            RememberDefaultBranch = rememberDefaultBranch;
        }

        public string Channel { get; }

        public bool AutoUpdateEnabled { get; }

        public bool RememberDefaultBranch { get; }
    }
}
