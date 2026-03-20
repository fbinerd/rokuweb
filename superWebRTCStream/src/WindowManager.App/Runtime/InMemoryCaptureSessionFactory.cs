using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class InMemoryCaptureSessionFactory : ICaptureSessionFactory
{
    public Task<CaptureSession> StartAsync(WindowSession windowSession, CancellationToken cancellationToken)
    {
        var session = new CaptureSession
        {
            WindowSessionId = windowSession.Id,
            IsActive = true
        };

        return Task.FromResult(session);
    }

    public Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken)
    {
        captureSession.IsActive = false;
        return Task.CompletedTask;
    }
}
