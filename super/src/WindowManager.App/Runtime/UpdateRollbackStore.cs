using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

[DataContract]
public sealed class UpdateRollbackRecord
{
    [DataMember(Order = 1)]
    public string PreviousReleaseId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string PreviousVersion { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string BaseBackupZipPath { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public string AppSnapshotZipPath { get; set; } = string.Empty;

    [DataMember(Order = 5)]
    public string CreatedAtUtc { get; set; } = string.Empty;
}

public sealed class UpdateRollbackStore
{
    private readonly string _filePath = Path.Combine(AppDataPaths.Root, "update-rollback-history.json");

    public Task<IReadOnlyList<UpdateRollbackRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Task.FromResult<IReadOnlyList<UpdateRollbackRecord>>(Array.Empty<UpdateRollbackRecord>());
        }

        var serializer = new DataContractJsonSerializer(typeof(List<UpdateRollbackRecord>));
        using (var stream = File.OpenRead(_filePath))
        {
            var records = serializer.ReadObject(stream) as List<UpdateRollbackRecord> ?? new List<UpdateRollbackRecord>();
            return Task.FromResult<IReadOnlyList<UpdateRollbackRecord>>(records);
        }
    }

    public Task SaveAsync(IEnumerable<UpdateRollbackRecord> records, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? AppDataPaths.Root);
        var serializer = new DataContractJsonSerializer(typeof(List<UpdateRollbackRecord>));
        using (var stream = File.Create(_filePath))
        {
            serializer.WriteObject(stream, records.ToList());
        }

        return Task.CompletedTask;
    }

    public async Task AddAsync(UpdateRollbackRecord record, CancellationToken cancellationToken)
    {
        var records = (await LoadAsync(cancellationToken)).ToList();
        records.Insert(0, record);
        records = records
            .GroupBy(x => x.AppSnapshotZipPath, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(10)
            .ToList();
        await SaveAsync(records, cancellationToken);
    }

    public async Task<UpdateRollbackRecord?> GetLatestAsync(CancellationToken cancellationToken)
    {
        return (await LoadAsync(cancellationToken)).FirstOrDefault();
    }

    public async Task RemoveAsync(UpdateRollbackRecord record, CancellationToken cancellationToken)
    {
        var records = (await LoadAsync(cancellationToken))
            .Where(x => !string.Equals(x.AppSnapshotZipPath, record.AppSnapshotZipPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await SaveAsync(records, cancellationToken);
    }
}
