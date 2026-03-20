using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.Core.Abstractions;

public interface IDisplayDiscoveryService
{
    Task<IReadOnlyList<DisplayTarget>> DiscoverAsync(CancellationToken cancellationToken);
}
