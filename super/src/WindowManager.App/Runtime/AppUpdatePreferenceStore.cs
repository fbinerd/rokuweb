using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

public sealed class AppUpdatePreferenceStore
{
    private readonly string _settingsFilePath;

    public AppUpdatePreferenceStore()
    {
        var root = AppDataPaths.Root;

        _settingsFilePath = Path.Combine(root, "update-settings.json");
    }

    public Task<AppUpdatePreferences> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return Task.FromResult(new AppUpdatePreferences());
        }

        var serializer = new DataContractJsonSerializer(typeof(AppUpdatePreferences));
        using (var stream = File.OpenRead(_settingsFilePath))
        {
            var preferences = serializer.ReadObject(stream) as AppUpdatePreferences;
            return Task.FromResult(preferences ?? new AppUpdatePreferences());
        }
    }

    public Task SaveAsync(AppUpdatePreferences preferences, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath)!;
        Directory.CreateDirectory(directory);

        var serializer = new DataContractJsonSerializer(typeof(AppUpdatePreferences));
        using (var stream = File.Create(_settingsFilePath))
        {
            serializer.WriteObject(stream, preferences);
        }

        return Task.CompletedTask;
    }
}

[DataContract]
public sealed class AppUpdatePreferences
{
    [DataMember(Order = 1)]
    public bool AutoUpdateEnabled { get; set; } = true;
}
