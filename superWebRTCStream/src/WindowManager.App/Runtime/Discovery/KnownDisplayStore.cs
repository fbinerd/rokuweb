using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WindowManager.App.Runtime.Discovery;

public sealed class KnownDisplayStore
{
    private readonly string _filePath;

    public KnownDisplayStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowManagerBroadcast");

        _filePath = Path.Combine(root, "known-displays.json");
    }

    public Task<IReadOnlyList<KnownDisplayRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return Task.FromResult<IReadOnlyList<KnownDisplayRecord>>(Array.Empty<KnownDisplayRecord>());
        }

        var serializer = new DataContractJsonSerializer(typeof(List<KnownDisplayRecord>));
        using (var stream = File.OpenRead(_filePath))
        {
            var records = serializer.ReadObject(stream) as List<KnownDisplayRecord> ?? new List<KnownDisplayRecord>();
            return Task.FromResult<IReadOnlyList<KnownDisplayRecord>>(records);
        }
    }

    public Task SaveAsync(IEnumerable<KnownDisplayRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? string.Empty;
        Directory.CreateDirectory(directory);

        var serializer = new DataContractJsonSerializer(typeof(List<KnownDisplayRecord>));
        using (var stream = File.Create(_filePath))
        {
            serializer.WriteObject(stream, records.OrderBy(x => x.Name).ToList());
        }

        return Task.CompletedTask;
    }

    public async Task RemoveAsync(Guid displayId, CancellationToken cancellationToken)
    {
        var records = (await LoadAsync(cancellationToken)).Where(x => x.Id != displayId).ToList();
        await SaveAsync(records, cancellationToken);
    }
}
