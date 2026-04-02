using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime
{
    public static partial class DependencyChecker
    {
        public static async Task EnsureDependenciesWithProgressAsync(string toolsDir, Action<string, double> progress)
        {
            try
            {
                progress("Verificando dependencias...", 0);
                var plan = PlanMissingDependencies(toolsDir);
                await DownloadDependenciesAsync(plan, (message, value) => progress(message, value * 0.7), CancellationToken.None);
                await InstallDownloadedDependenciesAsync(plan, (message, value) => progress(message, 70 + (value * 0.3)), CancellationToken.None);
                progress("Dependencias prontas!", 100);
            }
            catch (Exception ex)
            {
                progress($"Erro: {ex.Message}", 0);
                throw;
            }
        }

        public static async Task DownloadDependenciesAsync(DependencyDownloadPlan plan, Action<string, double> progress, CancellationToken cancellationToken)
        {
            if (plan is null || !plan.HasPendingDownloads)
            {
                progress("Dependencias ja existem.", 100);
                return;
            }

            Directory.CreateDirectory(plan.TempRoot);
            using var httpClient = new HttpClient();
            var total = plan.Items.Length;

            for (var i = 0; i < total; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = plan.Items[i];
                var baseProgress = (i * 100.0) / total;
                var span = 100.0 / total;
                var packageName = RequiredTools[item.Index] == "ffmpeg.exe"
                    ? "ffmpeg.zip"
                    : item.DisplayName;
                var statusLabel = $"Baixando pacotes {i + 1}/{total}...";
                var connectingLabel = "Buscando pacotes...";

                progress(connectingLabel, baseProgress);
                await HttpDownloadHelper.DownloadToFileAsync(
                    httpClient,
                    item.DownloadUrl,
                    item.DownloadPath,
                    statusLabel,
                    packageName + " -",
                    (message, value) => progress(message, baseProgress + (span * value / 100.0)),
                    cancellationToken).ConfigureAwait(false);
                progress($"{item.DisplayName} baixado.", baseProgress + span);
            }
        }

        public static Task InstallDownloadedDependenciesAsync(DependencyDownloadPlan plan, Action<string, double> progress, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                if (plan is null || !plan.HasPendingDownloads)
                {
                    progress("Dependencias ja existem.", 100);
                    return;
                }

                var total = plan.Items.Length;
                for (var i = 0; i < total; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = plan.Items[i];
                    var baseProgress = (i * 100.0) / total;
                    var span = 100.0 / total;

                    Directory.CreateDirectory(item.ToolSubDir);

                    if (RequiredTools[item.Index] == "ffmpeg.exe")
                    {
                        progress("Extraindo ffmpeg...", baseProgress);
                        System.IO.Compression.ZipFile.ExtractToDirectory(item.DownloadPath, item.ToolSubDir);
                        var ffmpegExe = Directory.GetFiles(item.ToolSubDir, "ffmpeg.exe", SearchOption.AllDirectories);
                        if (ffmpegExe.Length > 0 && !string.Equals(ffmpegExe[0], item.ToolPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(item.ToolPath))
                            {
                                File.Delete(item.ToolPath);
                            }

                            File.Copy(ffmpegExe[0], item.ToolPath);
                            File.Delete(ffmpegExe[0]);
                        }

                        if (File.Exists(item.DownloadPath))
                        {
                            File.Delete(item.DownloadPath);
                        }
                    }
                    else
                    {
                        progress($"Instalando {item.DisplayName}...", baseProgress);
                        if (File.Exists(item.ToolPath))
                        {
                            File.Delete(item.ToolPath);
                        }

                        File.Copy(item.DownloadPath, item.ToolPath, overwrite: true);
                        if (File.Exists(item.DownloadPath))
                        {
                            File.Delete(item.DownloadPath);
                        }
                    }

                    progress($"{item.DisplayName} pronto!", baseProgress + span);
                }

                if (Directory.Exists(plan.TempRoot))
                {
                    Directory.Delete(plan.TempRoot, recursive: true);
                }
            }, cancellationToken);
        }
    }
}
