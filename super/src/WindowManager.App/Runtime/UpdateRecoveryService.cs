using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime;

[DataContract]
public sealed class PendingUpdateRecoveryRecord
{
    [DataMember(Order = 1)]
    public string BackupZipPath { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string ReleaseId { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string CreatedAtUtc { get; set; } = string.Empty;
}

public sealed class UpdateRecoveryService
{
    public Task<PendingUpdateRecoveryRecord?> LoadPendingAsync(CancellationToken cancellationToken)
    {
        var path = AppDataPaths.PendingUpdateRecoveryPath;
        if (!File.Exists(path))
        {
            return Task.FromResult<PendingUpdateRecoveryRecord?>(null);
        }

        var serializer = new DataContractJsonSerializer(typeof(PendingUpdateRecoveryRecord));
        using (var stream = File.OpenRead(path))
        {
            return Task.FromResult(serializer.ReadObject(stream) as PendingUpdateRecoveryRecord);
        }
    }

    public Task SavePendingAsync(PendingUpdateRecoveryRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppDataPaths.Root);
        var serializer = new DataContractJsonSerializer(typeof(PendingUpdateRecoveryRecord));
        using (var stream = File.Create(AppDataPaths.PendingUpdateRecoveryPath))
        {
            serializer.WriteObject(stream, record);
        }

        return Task.CompletedTask;
    }

    public Task ClearPendingAsync(CancellationToken cancellationToken)
    {
        var path = AppDataPaths.PendingUpdateRecoveryPath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public string BuildAutomaticBackupPath(string releaseId)
    {
        Directory.CreateDirectory(AppDataPaths.BackupsRoot);
        var normalizedRelease = string.IsNullOrWhiteSpace(releaseId) ? "unknown" : releaseId.Trim();
        return Path.Combine(
            AppDataPaths.BackupsRoot,
            string.Format("pre-update-{0:yyyyMMdd-HHmmss}-{1}.zip", DateTime.Now, normalizedRelease));
    }
}
