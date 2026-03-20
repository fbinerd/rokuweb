using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

public sealed class AppSelfUpdateService
{
    private static readonly HttpClient HttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task<AppSelfUpdateResult> DownloadAndPrepareAsync(AppUpdateCheckResult update, CancellationToken cancellationToken)
    {
        if (update is null || !update.UpdateAvailable || string.IsNullOrWhiteSpace(update.RecommendedPackageUrl))
        {
            return AppSelfUpdateResult.Failure("Nenhuma atualizacao disponivel para aplicar.");
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "WindowManagerBroadcast",
            "updates",
            update.LatestReleaseId);

        var packageFileName = Path.GetFileName(new Uri(update.RecommendedPackageUrl).AbsolutePath);
        var packagePath = Path.Combine(tempRoot, packageFileName);
        var extractDirectory = Path.Combine(tempRoot, "extracted");
        var scriptPath = Path.Combine(tempRoot, "apply-update.cmd");
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Path.Combine(targetDirectory, "WindowManager.App.exe");

        Directory.CreateDirectory(tempRoot);

        using (var response = await HttpClient.GetAsync(update.RecommendedPackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var destinationStream = File.Create(packagePath))
            {
                await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
            }
        }

        if (Directory.Exists(extractDirectory))
        {
            Directory.Delete(extractDirectory, recursive: true);
        }

        ZipFile.ExtractToDirectory(packagePath, extractDirectory);

        var currentProcess = Process.GetCurrentProcess();
        var scriptContent = BuildUpdateScript(
            currentProcess.Id,
            extractDirectory,
            targetDirectory,
            exePath);

        File.WriteAllText(scriptPath, scriptContent, Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            WorkingDirectory = tempRoot,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        return AppSelfUpdateResult.Success(
            update.RecommendedPackageUrl,
            packagePath,
            extractDirectory,
            scriptPath);
    }

    private static string BuildUpdateScript(int processId, string sourceDirectory, string targetDirectory, string executablePath)
    {
        string Escape(string value) => value.Replace("\"", "\"\"");

        return string.Join(
            Environment.NewLine,
            new[]
            {
                "@echo off",
                "setlocal",
                string.Format("set \"SOURCE={0}\"", Escape(sourceDirectory)),
                string.Format("set \"TARGET={0}\"", Escape(targetDirectory)),
                string.Format("set \"EXE={0}\"", Escape(executablePath)),
                string.Format("set \"PID={0}\"", processId),
                ":waitloop",
                "tasklist /FI \"PID eq %PID%\" | findstr /I \"%PID%\" >nul",
                "if not errorlevel 1 (",
                "  timeout /t 1 /nobreak >nul",
                "  goto waitloop",
                ")",
                "robocopy \"%SOURCE%\" \"%TARGET%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul",
                "start \"\" \"%EXE%\"",
                "endlocal"
            });
    }
}

public sealed class AppSelfUpdateResult
{
    private AppSelfUpdateResult()
    {
    }

    public bool Succeeded { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string PackageUrl { get; private set; } = string.Empty;
    public string PackagePath { get; private set; } = string.Empty;
    public string ExtractDirectory { get; private set; } = string.Empty;
    public string ScriptPath { get; private set; } = string.Empty;

    public static AppSelfUpdateResult Success(string packageUrl, string packagePath, string extractDirectory, string scriptPath)
    {
        return new AppSelfUpdateResult
        {
            Succeeded = true,
            Message = "Atualizacao baixada e pronta para aplicacao.",
            PackageUrl = packageUrl,
            PackagePath = packagePath,
            ExtractDirectory = extractDirectory,
            ScriptPath = scriptPath
        };
    }

    public static AppSelfUpdateResult Failure(string message)
    {
        return new AppSelfUpdateResult
        {
            Succeeded = false,
            Message = message ?? string.Empty
        };
    }
}
