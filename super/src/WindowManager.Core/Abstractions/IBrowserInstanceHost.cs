using System;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.Core.Abstractions;

public interface IBrowserInstanceHost
{
    Task<WindowSession> CreateAsync(Uri initialUri, CancellationToken cancellationToken, Guid? preferredId = null, string? preferredTitle = null);

    Task CloseAsync(Guid windowSessionId, CancellationToken cancellationToken);

    Task NavigateAsync(Guid windowSessionId, Uri uri, CancellationToken cancellationToken);
}
