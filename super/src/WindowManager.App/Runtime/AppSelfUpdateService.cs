using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        if (update is null || !update.UpdateAvailable)
        {
            return AppSelfUpdateResult.Failure("Nenhuma atualizacao disponivel para aplicar.");
        }

        var packageUrls = update.PackageUrls?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
        if (packageUrls.Length == 0 && !string.IsNullOrWhiteSpace(update.RecommendedPackageUrl))
        {
            packageUrls = new[] { update.RecommendedPackageUrl };
        }

        if (packageUrls.Length == 0)
        {
            return AppSelfUpdateResult.Failure("Manifesto sem pacotes validos para aplicar.");
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "WindowManagerBroadcast",
            "updates",
            update.LatestReleaseId);

        var extractRoot = Path.Combine(tempRoot, "extracted");
        var scriptPath = Path.Combine(tempRoot, "apply-update.cmd");
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Path.Combine(targetDirectory, "SuperPainel.exe");
        var extractedDirectories = new List<string>();
        var lastPackagePath = string.Empty;

        Directory.CreateDirectory(tempRoot);

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }

        Directory.CreateDirectory(extractRoot);

        for (var index = 0; index < packageUrls.Length; index++)
        {
            var packageUrl = packageUrls[index];
            var packageFileName = Path.GetFileName(new Uri(packageUrl).AbsolutePath);
            var packagePath = Path.Combine(tempRoot, string.Format("{0:D2}-{1}", index + 1, packageFileName));
            var extractDirectory = Path.Combine(extractRoot, string.Format("{0:D2}", index + 1));

            using (var response = await HttpClient.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var sourceStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var destinationStream = File.Create(packagePath))
                {
                    await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
                }
            }

            Directory.CreateDirectory(extractDirectory);
            ZipFile.ExtractToDirectory(packagePath, extractDirectory);
            extractedDirectories.Add(extractDirectory);
            lastPackagePath = packagePath;
        }

        var currentProcess = Process.GetCurrentProcess();
        var scriptContent = BuildUpdateScript(
            currentProcess.Id,
            extractedDirectories.ToArray(),
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
            string.Join(Environment.NewLine, packageUrls),
            lastPackagePath,
            extractRoot,
            scriptPath);
    }

    private static string BuildUpdateScript(int processId, string[] sourceDirectories, string targetDirectory, string executablePath)
    {
        string Escape(string value) => value.Replace("\"", "\"\"");

        var scriptLines = new List<string>
        {
            "@echo off",
            "setlocal",
            string.Format("set \"TARGET={0}\"", Escape(targetDirectory)),
            string.Format("set \"EXE={0}\"", Escape(executablePath)),
            string.Format("set \"PID={0}\"", processId),
            ":waitloop",
            "tasklist /FI \"PID eq %PID%\" | findstr /I \"%PID%\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto waitloop",
            ")"
        };

        foreach (var sourceDirectory in sourceDirectories)
        {
            scriptLines.Add(string.Format("robocopy \"{0}\" \"%TARGET%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul", Escape(sourceDirectory)));
        }

        scriptLines.Add("start \"\" \"%EXE%\"");
        scriptLines.Add("endlocal");

        return string.Join(Environment.NewLine, scriptLines);
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
