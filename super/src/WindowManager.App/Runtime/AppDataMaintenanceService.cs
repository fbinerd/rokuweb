using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

public sealed class AppDataMaintenanceService
{
    public string DataRoot => AppDataPaths.Root;

    public Task ExportAsync(string destinationZipPath, CancellationToken cancellationToken)
    {
        return ExportAsync(destinationZipPath, progress: null, cancellationToken);
    }

    public Task ExportAsync(string destinationZipPath, Action<string, double>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(destinationZipPath))
            {
                throw new InvalidOperationException("Informe um caminho valido para o backup.");
            }

            var destinationDirectory = Path.GetDirectoryName(destinationZipPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var files = Directory.Exists(DataRoot)
                ? Directory.GetFiles(DataRoot, "*", SearchOption.AllDirectories)
                    .Where(path => ShouldIncludeInBackup(path, destinationZipPath))
                    .ToArray()
                : Array.Empty<string>();

            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            using var archive = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);

            if (files.Length > 0)
            {
                var totalFiles = files.Length;

                for (var index = 0; index < totalFiles; index++)
                {
                    var file = files[index];
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = GetRelativePath(DataRoot, file);
                    var progressPercent = totalFiles == 0
                        ? 100
                        : Math.Min(99, ((index * 100.0) / totalFiles));
                    progress?.Invoke(
                        $"Criando backup... {index + 1}/{totalFiles}: {relativePath}",
                        progressPercent);
                    archive.CreateEntryFromFile(file, relativePath);
                }

                if (totalFiles > 0)
                {
                    progress?.Invoke("Backup criado com sucesso.", 100);
                }
            }
            else
            {
                progress?.Invoke("Backup sem arquivos para salvar.", 100);
            }
        }, cancellationToken);
    }

    public Task ImportAsync(string sourceZipPath, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(sourceZipPath) || !File.Exists(sourceZipPath))
            {
                throw new FileNotFoundException("Nao foi possivel localizar o backup informado.", sourceZipPath);
            }

            ResetCoreData();
            Directory.CreateDirectory(DataRoot);

            using var archive = ZipFile.OpenRead(sourceZipPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(entry.FullName))
                {
                    continue;
                }

                if (ShouldSkipImportEntry(entry.FullName))
                {
                    continue;
                }

                var destinationPath = Path.Combine(DataRoot, entry.FullName);
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }, cancellationToken);
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCoreData();
        }, cancellationToken);
    }

    private void ResetCoreData()
    {
        if (!Directory.Exists(DataRoot))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(DataRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "cef", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.GetFiles(DataRoot))
        {
            File.Delete(file);
        }

        ResetBrowserProfileData();
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

    private static bool ShouldIncludeInBackup(string path, string destinationZipPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (string.Equals(path, destinationZipPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsInsideCefDirectory(path) && !IsInsideBackupsDirectory(path);
    }

    private static bool IsInsideCefDirectory(string path)
    {
        var cefRoot = AppDataPaths.CefRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(cefRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideBrowserProfilesDirectory(string path)
    {
        var browserProfilesRoot = AppDataPaths.CefBrowserProfilesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(browserProfilesRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideBackupsDirectory(string path)
    {
        var backupsRoot = AppDataPaths.BackupsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(backupsRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSkipImportEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        return normalized.StartsWith("cef/", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetBrowserProfileData()
    {
        var profilesRoot = AppDataPaths.CefBrowserProfilesRoot;
        if (Directory.Exists(profilesRoot))
        {
            Directory.Delete(profilesRoot, recursive: true);
        }
    }
}
