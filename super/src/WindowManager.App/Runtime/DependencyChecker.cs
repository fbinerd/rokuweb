using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime
{
    public static partial class DependencyChecker
    {
        private static readonly string[] ToolFolders = { "yt-dlp", "ffmpeg" };
        private static readonly string[] RequiredTools = { "yt-dlp.exe", "ffmpeg.exe" };
        private static readonly string[] DownloadUrls = {
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
            "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
        };

        public static async Task EnsureDependenciesAsync(string toolsDir)
        {
            try
            {
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] EnsureDependenciesAsync START\n");
                Directory.CreateDirectory(toolsDir);
                for (int i = 0; i < RequiredTools.Length; i++)
                {
                    string toolSubDir = Path.Combine(toolsDir, ToolFolders[i]);
                    Directory.CreateDirectory(toolSubDir);
                    string toolPath = Path.Combine(toolSubDir, RequiredTools[i]);
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Checking {toolPath}\n");
                    if (!File.Exists(toolPath))
                    {
                        File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Downloading {RequiredTools[i]}...\n");
                        await DownloadToolAsync(i, toolPath, toolSubDir);
                        File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Downloaded {RequiredTools[i]}\n");
                    }
                    else
                    {
                        File.AppendAllText("startup.log", $"[{DateTime.Now:O}] {RequiredTools[i]} already exists\n");
                    }
                }
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] EnsureDependenciesAsync END\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] EnsureDependenciesAsync EXCEPTION: {ex}\n");
                throw;
            }
        }

        private static async Task DownloadToolAsync(int index, string toolPath, string toolSubDir)
        {
            try
            {
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] DownloadToolAsync START {RequiredTools[index]}\n");
                using var httpClient = new HttpClient();
                string url = DownloadUrls[index];
                if (RequiredTools[index] == "ffmpeg.exe")
                {
                    // Download and extract ffmpeg.exe from zip
                    string zipPath = Path.Combine(toolSubDir, "ffmpeg.zip");
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Downloading ffmpeg.zip from {url}\n");
                    using (var response = await httpClient.GetAsync(url))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                        {
                            await response.Content.CopyToAsync(fs);
                        }
                    }
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Extracting ffmpeg.zip\n");
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, toolSubDir);
                    // Try to find ffmpeg.exe in extracted folders
                    var ffmpegExe = Directory.GetFiles(toolSubDir, "ffmpeg.exe", SearchOption.AllDirectories);
                    if (ffmpegExe.Length > 0)
                    {
                        if (!string.Equals(ffmpegExe[0], toolPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(toolPath))
                                File.Delete(toolPath);
                            File.Copy(ffmpegExe[0], toolPath);
                            File.Delete(ffmpegExe[0]);
                        }
                    }
                    File.Delete(zipPath);
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] ffmpeg.exe ready\n");
                }
                else
                {
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] Downloading yt-dlp.exe from {url}\n");
                    using (var response = await httpClient.GetAsync(url))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var fs = new FileStream(toolPath, FileMode.Create, FileAccess.Write))
                        {
                            await response.Content.CopyToAsync(fs);
                        }
                    }
                    File.AppendAllText("startup.log", $"[{DateTime.Now:O}] yt-dlp.exe ready\n");
                }
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] DownloadToolAsync END {RequiredTools[index]}\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText("startup.log", $"[{DateTime.Now:O}] DownloadToolAsync EXCEPTION {RequiredTools[index]}: {ex}\n");
                throw;
            }
        }
    }
}
