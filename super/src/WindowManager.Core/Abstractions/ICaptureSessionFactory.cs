using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.Core.Abstractions;

public interface ICaptureSessionFactory
{
    Task<CaptureSession> StartAsync(WindowSession windowSession, CancellationToken cancellationToken);

    Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken);
}
