using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Profiles;

public sealed class ProfileStore
{
    private readonly string _profilesDirectory;
    private readonly string _lastProfileFilePath;
    private readonly string _defaultProfileFilePath;

    public ProfileStore()
    {
        var root = Runtime.AppDataPaths.Root;

        _profilesDirectory = Path.Combine(root, "Profiles");
        _lastProfileFilePath = Path.Combine(root, "last-profile.txt");
        _defaultProfileFilePath = Path.Combine(root, "default-profile.txt");
    }

    public Task<IReadOnlyList<string>> GetProfileNamesAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_profilesDirectory);

        IReadOnlyList<string> names = Directory
            .GetFiles(_profilesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x)
            .ToList();

        return Task.FromResult(names);
    }

    public Task<string> GetStartupProfileNameAsync(CancellationToken cancellationToken)
    {
        var defaultProfileName = ReadProfileNameFromFile(_defaultProfileFilePath);
        if (!string.IsNullOrWhiteSpace(defaultProfileName))
        {
            return Task.FromResult(defaultProfileName);
        }

        var lastProfileName = ReadProfileNameFromFile(_lastProfileFilePath);
        if (!string.IsNullOrWhiteSpace(lastProfileName))
        {
            return Task.FromResult(lastProfileName);
        }

        return Task.FromResult("default");
    }

    public Task<string?> GetDefaultProfileNameAsync(CancellationToken cancellationToken)
    {
        var defaultProfileName = ReadProfileNameFromFile(_defaultProfileFilePath);
        return Task.FromResult<string?>(string.IsNullOrWhiteSpace(defaultProfileName) ? null : defaultProfileName);
    }

    public Task SetLastProfileNameAsync(string profileName, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(_lastProfileFilePath)!);
        File.WriteAllText(_lastProfileFilePath, normalized, Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task SetDefaultProfileNameAsync(string profileName, CancellationToken cancellationToken)
    {
        var normalized = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(_defaultProfileFilePath)!);
        File.WriteAllText(_defaultProfileFilePath, normalized, Encoding.UTF8);
        return Task.CompletedTask;
    }

    public Task ClearDefaultProfileNameAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_defaultProfileFilePath))
        {
            File.Delete(_defaultProfileFilePath);
        }

        return Task.CompletedTask;
    }

    public Task<AppProfile?> LoadAsync(string profileName, CancellationToken cancellationToken)
    {
        var path = GetProfilePath(profileName);
        if (!File.Exists(path))
        {
            return Task.FromResult<AppProfile?>(null);
        }

        var serializer = new DataContractJsonSerializer(typeof(AppProfile));
        using (var stream = OpenReadWithRetry(path, cancellationToken))
        {
            var profile = serializer.ReadObject(stream) as AppProfile;
            if (profile is not null && AppProfileMigrator.Migrate(profile))
            {
                WriteProfile(path, profile);
            }

            return Task.FromResult(profile);
        }
    }

    public Task SaveAsync(AppProfile profile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_profilesDirectory);
        AppProfileMigrator.Migrate(profile);

        WriteProfile(GetProfilePath(profile.Name), profile);

        File.WriteAllText(_lastProfileFilePath, profile.Name, Encoding.UTF8);
        return Task.CompletedTask;
    }

    public async Task DeleteAsync(string profileName, CancellationToken cancellationToken)
    {
        var path = GetProfilePath(profileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var lastProfileName = ReadProfileNameFromFile(_lastProfileFilePath);
        if (string.Equals(lastProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(_lastProfileFilePath, "default", Encoding.UTF8);
        }

        var defaultProfileName = ReadProfileNameFromFile(_defaultProfileFilePath);
        if (string.Equals(defaultProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            await ClearDefaultProfileNameAsync(cancellationToken);
        }
    }

    private static string ReadProfileNameFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        return File.ReadAllText(path, Encoding.UTF8).Trim();
    }

    private string GetProfilePath(string profileName)
    {
        var safeName = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalid, '_');
        }

        return Path.Combine(_profilesDirectory, safeName + ".json");
    }

    private static void WriteProfile(string path, AppProfile profile)
    {
        var serializer = new DataContractJsonSerializer(typeof(AppProfile));
        using (var stream = File.Create(path))
        {
            serializer.WriteObject(stream, profile);
        }
    }

    private static FileStream OpenReadWithRetry(string path, CancellationToken cancellationToken)
    {
        const int attempts = 6;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(200 * attempt);
            }
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }
}

