using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.App.Profiles;

public sealed class ActiveSessionStore
{
    private readonly string _filePath;

    public ActiveSessionStore()
    {
        _filePath = Path.Combine(Runtime.AppDataPaths.Root, "active-sessions.json");
    }

    public Task<IReadOnlyList<ActiveSessionRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Task.FromResult<IReadOnlyList<ActiveSessionRecord>>(Array.Empty<ActiveSessionRecord>());
        }

        var serializer = new DataContractJsonSerializer(typeof(List<ActiveSessionRecord>));
        using (var stream = File.OpenRead(_filePath))
        {
            var records = serializer.ReadObject(stream) as List<ActiveSessionRecord> ?? new List<ActiveSessionRecord>();
            return Task.FromResult<IReadOnlyList<ActiveSessionRecord>>(records);
        }
    }

    public Task SaveAsync(IEnumerable<ActiveSessionRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
        Directory.CreateDirectory(directory);

        var serializer = new DataContractJsonSerializer(typeof(List<ActiveSessionRecord>));
        using (var stream = File.Create(_filePath))
        {
            serializer.WriteObject(stream, records.OrderBy(x => x.Name).ToList());
        }

        return Task.CompletedTask;
    }
}

[DataContract]
public sealed class ActiveSessionRecord
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Name { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string ProfileName { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string StartedAtUtc { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public List<ActiveSessionWindowRecord> Windows { get; set; } = new List<ActiveSessionWindowRecord>();

    [DataMember(Order = 6)]
    public List<ActiveSessionDisplayBindingRecord> BoundDisplays { get; set; } = new List<ActiveSessionDisplayBindingRecord>();
}

[DataContract]
public sealed class ActiveSessionWindowRecord
{
    [DataMember(Order = 1)]
    public Guid Id { get; set; }

    [DataMember(Order = 2)]
    public string Title { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string InitialUrl { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public WindowSessionState State { get; set; }

    [DataMember(Order = 5)]
    public Guid? AssignedTargetId { get; set; }

    [DataMember(Order = 6)]
    public RenderResolutionMode BrowserResolutionMode { get; set; }

    [DataMember(Order = 7)]
    public int BrowserManualWidth { get; set; }

    [DataMember(Order = 8)]
    public int BrowserManualHeight { get; set; }

    [DataMember(Order = 9)]
    public RenderResolutionMode TargetResolutionMode { get; set; }

    [DataMember(Order = 10)]
    public int TargetManualWidth { get; set; }

    [DataMember(Order = 11)]
    public int TargetManualHeight { get; set; }

    [DataMember(Order = 12)]
    public bool IsWebRtcPublishingEnabled { get; set; }

    [DataMember(Order = 13)]
    public bool IsPrimaryExclusive { get; set; }

    [DataMember(Order = 14)]
    public bool IsNavigationBarEnabled { get; set; }

    [DataMember(Order = 15)]
    public string BrowserProfileName { get; set; } = string.Empty;
}

[DataContract]
public sealed class ActiveSessionDisplayBindingRecord
{
    [DataMember(Order = 1)]
    public Guid DisplayTargetId { get; set; }

    [DataMember(Order = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string NetworkAddress { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string DeviceUniqueId { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public string BindingName { get; set; } = string.Empty;
}
