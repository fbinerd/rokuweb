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
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowManagerBroadcast");

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

        return Task.FromResult("default");
    }

    public Task<string?> GetDefaultProfileNameAsync(CancellationToken cancellationToken)
    {
        var defaultProfileName = ReadProfileNameFromFile(_defaultProfileFilePath);
        return Task.FromResult<string?>(string.IsNullOrWhiteSpace(defaultProfileName) ? null : defaultProfileName);
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
        using (var stream = File.OpenRead(path))
        {
            return Task.FromResult(serializer.ReadObject(stream) as AppProfile);
        }
    }

    public Task SaveAsync(AppProfile profile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_profilesDirectory);

        var serializer = new DataContractJsonSerializer(typeof(AppProfile));
        using (var stream = File.Create(GetProfilePath(profile.Name)))
        {
            serializer.WriteObject(stream, profile);
        }

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
}

