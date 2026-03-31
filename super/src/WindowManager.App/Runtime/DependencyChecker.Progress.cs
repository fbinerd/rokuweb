using System;
using System.IO;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime
{
    public static partial class DependencyChecker
    {
        public static async Task EnsureDependenciesWithProgressAsync(string toolsDir, Action<string, double> progress)
        {
            try
            {
                progress("Verificando dependências...", 0);
                Directory.CreateDirectory(toolsDir);
                int total = RequiredTools.Length;
                for (int i = 0; i < total; i++)
                {
                    string toolSubDir = Path.Combine(toolsDir, ToolFolders[i]);
                    Directory.CreateDirectory(toolSubDir);
                    string toolPath = Path.Combine(toolSubDir, RequiredTools[i]);
                    double pct = (i * 100.0) / total;
                    if (!File.Exists(toolPath))
                    {
                        progress($"Baixando {RequiredTools[i]}...", pct);
                        await DownloadToolWithProgressAsync(i, toolPath, toolSubDir, (msg, p) => progress(msg, pct + p / total * 100.0 / total));
                        progress($"{RequiredTools[i]} pronto!", ((i + 1) * 100.0) / total);
                    }
                    else
                    {
                        progress($"{RequiredTools[i]} já existe", ((i + 1) * 100.0) / total);
                    }
                }
                progress("Dependências prontas!", 100);
            }
            catch (Exception ex)
            {
                progress($"Erro: {ex.Message}", 0);
                throw;
            }
        }

        private static async Task DownloadToolWithProgressAsync(int index, string toolPath, string toolSubDir, Action<string, double> progress)
        {
            if (RequiredTools[index] == "ffmpeg.exe")
            {
                string url = DownloadUrls[index];
                string zipPath = Path.Combine(toolSubDir, "ffmpeg.zip");
                progress("Baixando ffmpeg.zip...", 0);
                using (var httpClient = new System.Net.Http.HttpClient())
                using (var response = await httpClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 1;
                    using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, n);
                            read += n;
                            progress($"Baixando ffmpeg.zip...", Math.Min(99, (read * 100.0) / total));
                        }
                    }
                }
                progress("Extraindo ffmpeg...", 99);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolSubDir);
                var ffmpegExe = Directory.GetFiles(toolSubDir, "ffmpeg.exe", SearchOption.AllDirectories);
                if (ffmpegExe.Length > 0 && !string.Equals(ffmpegExe[0], toolPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(toolPath)) File.Delete(toolPath);
                    File.Copy(ffmpegExe[0], toolPath);
                    File.Delete(ffmpegExe[0]);
                }
                File.Delete(zipPath);
                progress("ffmpeg.exe pronto!", 100);
            }
            else
            {
                string url = DownloadUrls[index];
                progress("Baixando yt-dlp.exe...", 0);
                using (var httpClient = new System.Net.Http.HttpClient())
                using (var response = await httpClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 1;
                    using (var fs = new FileStream(toolPath, FileMode.Create, FileAccess.Write))
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int n;
                        while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, n);
                            read += n;
                            progress($"Baixando yt-dlp.exe...", Math.Min(99, (read * 100.0) / total));
                        }
                    }
                }
                progress("yt-dlp.exe pronto!", 100);
            }
        }
    }
}
