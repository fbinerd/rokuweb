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
        if (string.IsNullOrWhiteSpace(destinationZipPath))
        {
            throw new InvalidOperationException("Informe um caminho valido para o backup.");
        }

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

        if (Directory.Exists(DataRoot))
        {
            foreach (var file in Directory.GetFiles(DataRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = GetRelativePath(DataRoot, file);
                archive.CreateEntryFromFile(file, relativePath);
            }
        }

        return Task.CompletedTask;
    }

    public Task ImportAsync(string sourceZipPath, CancellationToken cancellationToken)
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

        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResetCoreData();
        return Task.CompletedTask;
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

        var cefRoot = AppDataPaths.CefRoot;
        if (!Directory.Exists(cefRoot))
        {
            return;
        }

        var cacheDirectory = Path.Combine(cefRoot, "cache");
        foreach (var entry in Directory.GetFileSystemEntries(cefRoot).Where(x => !string.Equals(x, cacheDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else if (File.Exists(entry))
            {
                File.Delete(entry);
            }
        }
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
