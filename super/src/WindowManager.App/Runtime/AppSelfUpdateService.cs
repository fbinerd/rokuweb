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

    public async Task<AppSelfUpdateResult> DownloadAndPrepareAsync(
        AppUpdateCheckResult update,
        CancellationToken cancellationToken)
    {
        return await DownloadAndPrepareAsync(update, progress: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSelfUpdateResult> DownloadAndPrepareAsync(
        AppUpdateCheckResult update,
        Action<string, double>? progress,
        CancellationToken cancellationToken,
        string restartArguments = "")
    {
        var downloadResult = await DownloadPackagesAsync(update, progress, cancellationToken).ConfigureAwait(false);
        return await PrepareDownloadedPackagesAsync(update, downloadResult, progress, cancellationToken, restartArguments).ConfigureAwait(false);
    }

    public async Task<AppSelfUpdateDownloadResult> DownloadPackagesAsync(
        AppUpdateCheckResult update,
        Action<string, double>? progress,
        CancellationToken cancellationToken)
    {
        if (update is null || !update.UpdateAvailable)
        {
            return AppSelfUpdateDownloadResult.Failure("Nenhuma atualização disponível para aplicar.");
        }

        var packageUrls = ResolvePackageUrls(update);
        if (packageUrls.Length == 0)
        {
            return AppSelfUpdateDownloadResult.Failure("Manifesto sem pacotes válidos para aplicar.");
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "WindowManagerBroadcast",
            "updates",
            update.LatestReleaseId);

        Directory.CreateDirectory(tempRoot);

        var packagePaths = new List<string>();
        for (var index = 0; index < packageUrls.Length; index++)
        {
            var packageUrl = packageUrls[index];
            var packageFileName = Path.GetFileName(new Uri(packageUrl).AbsolutePath);
            var packagePath = Path.Combine(tempRoot, string.Format("{0:D2}-{1}", index + 1, packageFileName));
            var packageBaseProgress = packageUrls.Length == 0 ? 0 : (index * 100.0) / packageUrls.Length;
            var packageProgressSpan = packageUrls.Length == 0 ? 100.0 : 100.0 / packageUrls.Length;
            var statusLabel = string.Format("Baixando pacotes {0}/{1}...", index + 1, packageUrls.Length);
            var connectingLabel = "Buscando pacotes...";

            progress?.Invoke(connectingLabel, packageBaseProgress);
            await HttpDownloadHelper.DownloadToFileAsync(
                HttpClient,
                packageUrl,
                packagePath,
                statusLabel,
                packageFileName + " -",
                (message, value) => progress?.Invoke(message, packageBaseProgress + (packageProgressSpan * value / 100.0)),
                cancellationToken).ConfigureAwait(false);
            progress?.Invoke(string.Format("Pacote {0}/{1} baixado.", index + 1, packageUrls.Length), packageBaseProgress + packageProgressSpan);
            packagePaths.Add(packagePath);
        }

        return AppSelfUpdateDownloadResult.Success(tempRoot, packageUrls, packagePaths.ToArray());
    }

    public async Task<AppSelfUpdateResult> PrepareDownloadedPackagesAsync(
        AppUpdateCheckResult update,
        AppSelfUpdateDownloadResult downloadResult,
        Action<string, double>? progress,
        CancellationToken cancellationToken,
        string restartArguments = "")
    {
        if (downloadResult is null || !downloadResult.Succeeded)
        {
            return AppSelfUpdateResult.Failure(downloadResult?.Message ?? "Pacotes de atualização não foram baixados.");
        }

        var extractRoot = Path.Combine(downloadResult.TempRoot, "extracted");
        var scriptPath = Path.Combine(downloadResult.TempRoot, "apply-update.ps1");
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Path.Combine(targetDirectory, "SuperPainel.exe");
        var extractedDirectories = new List<string>();
        var packagePaths = downloadResult.PackagePaths ?? Array.Empty<string>();

        if (Directory.Exists(extractRoot))
        {
            Directory.Delete(extractRoot, recursive: true);
        }

        Directory.CreateDirectory(extractRoot);

        for (var index = 0; index < packagePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = packagePaths[index];
            var extractDirectory = Path.Combine(extractRoot, string.Format("{0:D2}", index + 1));
            progress?.Invoke(string.Format("Extraindo pacote {0}/{1}...", index + 1, packagePaths.Length), 20 + ((index * 60.0) / Math.Max(1, packagePaths.Length)));
            Directory.CreateDirectory(extractDirectory);
            ZipFile.ExtractToDirectory(packagePath, extractDirectory);
            extractedDirectories.Add(extractDirectory);
        }

        var expectedExecutableHash = string.Empty;
        var extractedExecutablePath = Path.Combine(extractedDirectories.LastOrDefault() ?? string.Empty, "SuperPainel.exe");
        if (File.Exists(extractedExecutablePath))
        {
            expectedExecutableHash = ComputeSha256(extractedExecutablePath);
        }

        var currentProcess = Process.GetCurrentProcess();
        var scriptContent = BuildUpdateScript(
            currentProcess.Id,
            extractedDirectories.ToArray(),
            targetDirectory,
            exePath,
            expectedExecutableHash,
            Path.Combine(downloadResult.TempRoot, "apply-update.log"),
            restartArguments);

        File.WriteAllText(scriptPath, scriptContent, Encoding.ASCII);
        progress?.Invoke("Finalizando preparacao da atualizacao...", 95);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = string.Format(
                "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\"",
                scriptPath),
            WorkingDirectory = downloadResult.TempRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        progress?.Invoke("Atualizacao preparada. Reiniciando aplicacao...", 100);
        return AppSelfUpdateResult.Success(
            string.Join(Environment.NewLine, downloadResult.PackageUrls ?? Array.Empty<string>()),
            packagePaths.LastOrDefault() ?? string.Empty,
            extractRoot,
            scriptPath);
    }

    public Task<AppSelfUpdateResult> PrepareLocalPackageAsync(string packageZipPath, string? restoreBackupZipPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageZipPath) || !File.Exists(packageZipPath))
        {
            return Task.FromResult(AppSelfUpdateResult.Failure("Nao foi possivel localizar o pacote local para rollback."));
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "WindowManagerBroadcast",
            "rollback",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));

        var extractRoot = Path.Combine(tempRoot, "extracted");
        var scriptPath = Path.Combine(tempRoot, "apply-rollback.ps1");
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var exePath = Path.Combine(targetDirectory, "SuperPainel.exe");

        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(packageZipPath, extractRoot);

        var expectedExecutableHash = string.Empty;
        var extractedExecutablePath = Path.Combine(extractRoot, "SuperPainel.exe");
        if (File.Exists(extractedExecutablePath))
        {
            expectedExecutableHash = ComputeSha256(extractedExecutablePath);
        }

        var currentProcess = Process.GetCurrentProcess();
        var scriptContent = BuildUpdateScript(
            currentProcess.Id,
            new[] { extractRoot },
            targetDirectory,
            exePath,
            expectedExecutableHash,
            Path.Combine(tempRoot, "apply-rollback.log"),
            BuildRestartArguments(restoreBackupZipPath));

        File.WriteAllText(scriptPath, scriptContent, Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = string.Format("-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{0}\"", scriptPath),
            WorkingDirectory = tempRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        return Task.FromResult(AppSelfUpdateResult.Success(packageZipPath, packageZipPath, extractRoot, scriptPath));
    }

    private static string BuildUpdateScript(
        int processId,
        string[] sourceDirectories,
        string targetDirectory,
        string executablePath,
        string expectedExecutableHash,
        string logPath,
        string restartArguments = "")
    {
        string EscapePowerShell(string value) => value.Replace("'", "''");
        string ToLiteralArray(IEnumerable<string> values) => string.Join(", ", values.Select(x => "'" + EscapePowerShell(x) + "'"));

        var scriptLines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            string.Format("$target = '{0}'", EscapePowerShell(targetDirectory)),
            string.Format("$exe = '{0}'", EscapePowerShell(executablePath)),
            string.Format("$expectedExecutableHash = '{0}'", EscapePowerShell(expectedExecutableHash ?? string.Empty)),
            string.Format("$logPath = '{0}'", EscapePowerShell(logPath)),
            string.Format("$processId = {0}", processId),
            string.Format("$sourceDirectories = @({0})", ToLiteralArray(sourceDirectories)),
            "function Write-UpdateLog([string]$message) {",
            "  $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'",
            "  Add-Content -Path $logPath -Value (\"[$timestamp] \" + $message)",
            "}",
            "Write-UpdateLog 'Atualizacao iniciada.'",
            "while (Get-Process -Id $processId -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 1 }",
            "Start-Sleep -Seconds 1",
            "try {",
            "  Get-CimInstance Win32_Process -Filter \"name = 'CefSharp.BrowserSubprocess.exe'\" -ErrorAction SilentlyContinue |",
            "    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase) } |",
            "    ForEach-Object {",
            "      Write-UpdateLog (\"Encerrando subprocesso CEF PID=\" + $_.ProcessId)",
            "      Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue",
            "    }",
            "} catch {",
            "  Write-UpdateLog (\"Falha ao encerrar subprocessos CEF: \" + $_.Exception.Message)",
            "}",
            "foreach ($sourceDirectory in $sourceDirectories) {",
            "  Write-UpdateLog (\"Copiando arquivos de \" + $sourceDirectory)",
            "  & robocopy $sourceDirectory $target /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
            "  $robocopyExitCode = $LASTEXITCODE",
            "  Write-UpdateLog (\"robocopy exit code=\" + $robocopyExitCode)",
            "  if ($robocopyExitCode -ge 8) {",
            "    throw \"Falha ao copiar arquivos da atualizacao. Codigo do robocopy: $robocopyExitCode\"",
            "  }",
            "}",
            "if (-not [string]::IsNullOrWhiteSpace($expectedExecutableHash)) {",
            "  if (-not (Test-Path $exe)) {",
            "    throw \"Executavel atualizado nao encontrado: $exe\"",
            "  }",
            "  $actualExecutableHash = (Get-FileHash -Path $exe -Algorithm SHA256).Hash",
            "  Write-UpdateLog (\"Hash esperado=\" + $expectedExecutableHash)",
            "  Write-UpdateLog (\"Hash atual=\" + $actualExecutableHash)",
            "  if (-not [string]::Equals($actualExecutableHash, $expectedExecutableHash, [System.StringComparison]::OrdinalIgnoreCase)) {",
            "    throw 'O executavel reiniciado nao corresponde ao pacote atualizado.'",
            "  }",
            "}",
            string.Format("$restartArguments = '{0}'", EscapePowerShell(restartArguments ?? string.Empty)),
            "Write-UpdateLog 'Atualizacao aplicada. Reiniciando SuperPainel.'",
            "if ([string]::IsNullOrWhiteSpace($restartArguments)) {",
            "  Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe)",
            "} else {",
            "  Start-Process -FilePath $exe -ArgumentList $restartArguments -WorkingDirectory (Split-Path -Parent $exe)",
            "}",
            "Write-UpdateLog 'Reinicio solicitado com sucesso.'"
        };

        return string.Join(Environment.NewLine, scriptLines);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static string BuildRestartArguments(string? restoreBackupZipPath)
    {
        if (string.IsNullOrWhiteSpace(restoreBackupZipPath))
        {
            return string.Empty;
        }

        return string.Format("--restore-backup \"{0}\"", restoreBackupZipPath.Replace("\"", "\"\""));
    }

    private static string[] ResolvePackageUrls(AppUpdateCheckResult update)
    {
        var packageUrls = update.PackageUrls?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
        if (packageUrls.Length == 0 && !string.IsNullOrWhiteSpace(update.RecommendedPackageUrl))
        {
            packageUrls = new[] { update.RecommendedPackageUrl };
        }

        return packageUrls;
    }
}

public sealed class AppSelfUpdateDownloadResult
{
    private AppSelfUpdateDownloadResult()
    {
    }

    public bool Succeeded { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string TempRoot { get; private set; } = string.Empty;
    public string[] PackageUrls { get; private set; } = Array.Empty<string>();
    public string[] PackagePaths { get; private set; } = Array.Empty<string>();

    public static AppSelfUpdateDownloadResult Success(string tempRoot, IEnumerable<string> packageUrls, IEnumerable<string> packagePaths)
    {
        return new AppSelfUpdateDownloadResult
        {
            Succeeded = true,
            TempRoot = tempRoot ?? string.Empty,
            PackageUrls = packageUrls?.ToArray() ?? Array.Empty<string>(),
            PackagePaths = packagePaths?.ToArray() ?? Array.Empty<string>(),
            Message = "Pacotes de update baixados com sucesso."
        };
    }

    public static AppSelfUpdateDownloadResult Failure(string message)
    {
        return new AppSelfUpdateDownloadResult
        {
            Succeeded = false,
            Message = message ?? string.Empty
        };
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
