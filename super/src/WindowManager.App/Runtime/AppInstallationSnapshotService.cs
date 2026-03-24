using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

public sealed class AppInstallationSnapshotService
{
    public Task ExportCurrentInstallationAsync(string destinationZipPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new InvalidOperationException("Informe um caminho valido para o snapshot da aplicacao.");
        }

        var sourceRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        if (File.Exists(destinationZipPath))
        {
            File.Delete(destinationZipPath);
        }

        using var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .Where(ShouldIncludeInSnapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GetRelativePath(sourceRoot, file);
            archive.CreateEntryFromFile(file, relativePath);
        }

        return Task.CompletedTask;
    }

    public string BuildSnapshotPath(string releaseId)
    {
        Directory.CreateDirectory(AppDataPaths.BackupsRoot);
        var normalizedRelease = string.IsNullOrWhiteSpace(releaseId) ? "unknown" : releaseId.Trim();
        return Path.Combine(
            AppDataPaths.BackupsRoot,
            string.Format("app-before-update-{0:yyyyMMdd-HHmmss}-{1}.zip", DateTime.Now, normalizedRelease));
    }

    private static bool ShouldIncludeInSnapshot(string path)
    {
        var fileName = Path.GetFileName(path);
        return !string.Equals(fileName, "startup.log", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(fileName, "cef.log", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRelativePath(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(path);
        }

        return path.Substring(normalizedRoot.Length).Replace("\\", "/");
    }
}
