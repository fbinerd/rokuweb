using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WindowManager.App;
using WindowManager.App.Profiles;
using WindowManager.App.Runtime;

namespace WindowManager.Launcher;

public partial class App : Application
{
    private const string SkipUpdaterArgument = "--skip-updater";
    private const string LaunchedByLauncherArgument = "--launched-by-launcher";

    private enum LauncherOutcome
    {
        LaunchInstalledApp,
        ExitAfterUpdate,
        Error
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startupLogPath = Path.Combine(AppContext.BaseDirectory, "startup.log");
        var profileStore = new ProfileStore();
        var preferenceStore = new AppUpdatePreferenceStore();
        WindowManager.App.SplashScreen? splash = null;

        void LogStep(string message)
        {
            File.AppendAllText(startupLogPath, $"[{DateTime.Now:O}] [Launcher] {message}{Environment.NewLine}");
        }

        try
        {
            splash = new WindowManager.App.SplashScreen();

            var startupProfileName = await profileStore.GetStartupProfileNameAsync(CancellationToken.None);
            var startupProfile = await profileStore.LoadAsync(startupProfileName, CancellationToken.None);
            var startupPreferences = await preferenceStore.LoadAsync(CancellationToken.None);
            var startupSettings = BuildStartupUpdateSettings(startupProfile, startupPreferences);
            var initialAutoStart = startupSettings.RememberDefaultBranch && startupSettings.AutoUpdateEnabled;

            splash.SetInstalledVersion(FormatInstalledVersion(startupSettings.Channel));
            splash.SetChannels(new[] { UpdateChannelNames.Stable, UpdateChannelNames.Develop, UpdateChannelNames.Local }, startupSettings.Channel);
            splash.SetSetDefaultBranchChecked(startupSettings.RememberDefaultBranch);
            splash.SetAutoUpdateChecked(startupSettings.AutoUpdateEnabled && startupSettings.RememberDefaultBranch);
            splash.SetCompactMode(initialAutoStart);
            splash.ShowProgressBar(false);
            splash.Show();

            var manifestService = new AppUpdateManifestService();
            var channels = new[] { UpdateChannelNames.Stable, UpdateChannelNames.Develop, UpdateChannelNames.Local };
            var prefetchedUpdates = await PrefetchUpdateInfoAsync(manifestService, channels, LogStep);
            if (prefetchedUpdates is null)
            {
                LaunchInstalledApp(e.Args);
                Shutdown(0);
                return;
            }

            await UpdateRemoteVersionAsync(splash, startupSettings.Channel, manifestService, prefetchedUpdates, LogStep);
            splash.ChannelCombo.SelectionChanged += async (_, __) =>
            {
                await UpdateRemoteVersionAsync(splash, splash.SelectedChannel, manifestService, prefetchedUpdates, LogStep);
            };

            async Task SaveSettingsAsync()
            {
                var selectedChannel = UpdateChannelNames.Normalize(splash.SelectedChannel);
                var autoUpdateChecked = splash.IsAutoUpdateChecked;
                var rememberDefaultBranch = splash.IsSetDefaultBranchChecked;
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

            async Task<LauncherOutcome> RunUpdateFlowAsync(string branch)
            {
                try
                {
                    void RunOnSplashUi(Action action)
                    {
                        if (splash.Dispatcher.CheckAccess())
                        {
                            action();
                        }
                        else
                        {
                            splash.Dispatcher.Invoke(action);
                        }
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

                    static int GetUpdateDownloadItemCount(AppUpdateCheckResult result)
                    {
                        if (result is null || !result.UpdateAvailable)
                        {
                            return 0;
                        }

                        if (result.PackageUrls is { Length: > 0 })
                        {
                            return result.PackageUrls.Length;
                        }

                        return string.IsNullOrWhiteSpace(result.RecommendedPackageUrl) ? 0 : 1;
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
                        return string.IsNullOrWhiteSpace(suffix) ? status : $"{status} {suffix}";
                    }

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
                            splash.ShowProgressBar(true);
                            splash.SetStatus(status, currentProgress);
                            splash.SetProgressDetails(currentProgressDetails);
                        });
                    }

                    double MapProgress(double phaseStart, double phaseEnd, double phaseProgress)
                    {
                        var normalized = Math.Max(0, Math.Min(100, phaseProgress)) / 100.0;
                        return phaseStart + ((phaseEnd - phaseStart) * normalized);
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

                    var dependencyPlan = DependencyChecker.PlanMissingDependencies(Path.Combine(AppContext.BaseDirectory, "tools"));
                    var updateDownloadItemCount = GetUpdateDownloadItemCount(updateResult);
                    var totalDownloadItems = updateDownloadItemCount + dependencyPlan.Items.Length;
                    var maintenanceService = new AppDataMaintenanceService();
                    var backupPath = string.Empty;

                    if (updateResult.UpdateAvailable)
                    {
                        try
                        {
                            backupPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "WindowManagerBroadcast",
                                "backups",
                                $"pre-update-{DateTime.Now:yyyyMMdd-HHmmss}-{updateResult.LatestReleaseId}.zip");
                            SetProgress("Criando backup antes da atualização...", 12);
                            await maintenanceService.ExportAsync(
                                backupPath,
                                (message, progress) => SetProgress(message, MapProgress(12, 22, progress)),
                                CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            SetProgress($"Falha ao criar backup: {ex.Message}", 22);
                        }
                    }

                    var selfUpdateService = new AppSelfUpdateService();
                    AppSelfUpdateDownloadResult? updateDownload = null;

                    if (updateResult.UpdateAvailable)
                    {
                        SetProgress("Buscando pacotes...", 25);
                        updateDownload = await selfUpdateService.DownloadPackagesAsync(
                            updateResult,
                            (message, progress) => SetProgress(RemapDownloadMessage(message, 0, totalDownloadItems), MapProgress(25, dependencyPlan.HasPendingDownloads ? 55 : 70, progress)),
                            CancellationToken.None);

                        if (!updateDownload.Succeeded)
                        {
                            SetProgress($"Falha ao baixar atualização: {updateDownload.Message}", 100);
                            return LauncherOutcome.Error;
                        }
                    }

                    if (dependencyPlan.HasPendingDownloads)
                    {
                        var offset = updateDownloadItemCount;
                        var start = updateResult.UpdateAvailable ? 55 : 25;
                        var end = updateResult.UpdateAvailable ? 70 : 70;
                        SetProgress("Buscando pacotes...", start);
                        await DependencyChecker.DownloadDependenciesAsync(
                            dependencyPlan,
                            (message, progress) => SetProgress(RemapDownloadMessage(message, offset, totalDownloadItems), MapProgress(start, end, progress)),
                            CancellationToken.None);
                    }

                    if (dependencyPlan.HasPendingDownloads)
                    {
                        SetProgress("Instalando dependências...", 70);
                        await DependencyChecker.InstallDownloadedDependenciesAsync(
                            dependencyPlan,
                            (message, progress) => SetProgress(message, MapProgress(70, updateResult.UpdateAvailable ? 82 : 100, progress)),
                            CancellationToken.None);
                    }

                    if (!updateResult.UpdateAvailable)
                    {
                        SetProgress("Dependências prontas!", 100);
                        return LauncherOutcome.LaunchInstalledApp;
                    }

                    SetProgress("Aplicando atualização...", 82);
                    var restartArguments = BuildLaunchArguments(e.Args);
                    var applyResult = await selfUpdateService.PrepareDownloadedPackagesAsync(
                        updateResult,
                        updateDownload!,
                        (message, progress) => SetProgress(message, MapProgress(82, 100, progress)),
                        CancellationToken.None,
                        restartArguments);

                    if (!applyResult.Succeeded)
                    {
                        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
                        {
                            SetProgress("Restaurando backup...", 95);
                            await maintenanceService.ImportAsync(backupPath, CancellationToken.None);
                        }

                        MessageBox.Show($"Falha ao aplicar atualização: {applyResult.Message}", "Erro de Atualização", MessageBoxButton.OK, MessageBoxImage.Error);
                        return LauncherOutcome.Error;
                    }

                    return LauncherOutcome.ExitAfterUpdate;
                }
                catch (Exception ex)
                {
                    LogStep($"Falha no launcher updater: {ex}");
                    MessageBox.Show(ex.ToString(), "Falha ao atualizar", MessageBoxButton.OK, MessageBoxImage.Error);
                    return LauncherOutcome.Error;
                }
            }

            var shouldAutoStart = splash.IsSetDefaultBranchChecked && splash.IsAutoUpdateChecked;
            LauncherOutcome outcome;
            var updateStarted = false;

            if (shouldAutoStart)
            {
                splash.SetInteractionEnabled(false);
                updateStarted = true;
                await SaveSettingsAsync();
                outcome = await RunUpdateFlowAsync(splash.SelectedChannel);
            }
            else
            {
                var completion = new TaskCompletionSource<LauncherOutcome>();
                splash.GetOkButton().Click += async (_, __) =>
                {
                    if (updateStarted)
                    {
                        return;
                    }

                    updateStarted = true;
                    splash.SetInteractionEnabled(false);
                    await SaveSettingsAsync();
                    var result = await RunUpdateFlowAsync(splash.SelectedChannel);
                    completion.TrySetResult(result);
                };

                outcome = await completion.Task;
            }

            if (splash.IsLoaded)
            {
                splash.Close();
            }

            if (outcome == LauncherOutcome.LaunchInstalledApp)
            {
                LaunchInstalledApp(e.Args);
                Shutdown(0);
                return;
            }

            if (outcome == LauncherOutcome.ExitAfterUpdate)
            {
                Shutdown(0);
                return;
            }

            Shutdown(-1);
        }
        catch (Exception ex)
        {
            LogStep($"Falha no launcher: {ex}");
            MessageBox.Show(ex.ToString(), "Falha ao iniciar launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
        finally
        {
            if (splash is not null && splash.IsLoaded)
            {
                splash.Close();
            }
        }
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

        return new StartupUpdateSettings(channel, autoUpdate && rememberDefaultBranch, rememberDefaultBranch);
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
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logStep("Prefetch de updates excedeu o timeout. Abrindo o app sem launcher updater.");
            return null;
        }
        catch (Exception ex)
        {
            logStep($"Falha ao buscar versões remotas no launcher: {ex}");
            return null;
        }
    }

    private static async Task UpdateRemoteVersionAsync(
        WindowManager.App.SplashScreen splash,
        string branch,
        AppUpdateManifestService manifestService,
        IReadOnlyDictionary<string, AppUpdateCheckResult> prefetchedUpdates,
        Action<string> logStep)
    {
        var normalizedBranch = UpdateChannelNames.Normalize(branch);

        if (prefetchedUpdates.TryGetValue(normalizedBranch, out var cached))
        {
            splash.SetRemoteVersion(FormatRemoteVersion(normalizedBranch, cached));
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
            logStep($"Falha ao buscar versão remota da branch {normalizedBranch}: {ex}");
            splash.SetRemoteVersion($"branch {normalizedBranch}: erro ao buscar versao");
        }
    }

    private static string FormatInstalledVersion(string branch)
    {
        var normalized = UpdateChannelNames.Normalize(branch);
        return $"branch {normalized}: {BuildVersionInfo.Version} ({BuildVersionInfo.ReleaseId})";
    }

    private static string FormatRemoteVersion(string branch, AppUpdateCheckResult? result)
    {
        var normalized = UpdateChannelNames.Normalize(branch);
        if (result is null)
        {
            return $"branch {normalized}: -";
        }

        if (!string.IsNullOrEmpty(result.LatestVersion) && !string.IsNullOrEmpty(result.LatestReleaseId))
        {
            return $"branch {normalized}: {result.LatestVersion} (build {result.LatestReleaseId})";
        }

        return !string.IsNullOrEmpty(result.LatestVersion)
            ? $"branch {normalized}: {result.LatestVersion}"
            : $"branch {normalized}: -";
    }

    private static void LaunchInstalledApp(string[] originalArgs)
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "SuperPainel.exe");
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("Não foi possível localizar o SuperPainel.exe.", exePath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = BuildLaunchArguments(originalArgs),
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false
        });
    }

    private static string BuildLaunchArguments(string[] originalArgs)
    {
        var items = new List<string>();
        var hasSkipUpdater = false;

        foreach (var arg in originalArgs ?? Array.Empty<string>())
        {
            if (string.Equals(arg, SkipUpdaterArgument, StringComparison.OrdinalIgnoreCase))
            {
                hasSkipUpdater = true;
            }

            if (string.Equals(arg, LaunchedByLauncherArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            items.Add(QuoteArgument(arg));
        }

        if (!hasSkipUpdater)
        {
            items.Add(SkipUpdaterArgument);
        }

        items.Add(LaunchedByLauncherArgument);

        return string.Join(" ", items);
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        if (!value.Contains(" ") && !value.Contains("\""))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
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
