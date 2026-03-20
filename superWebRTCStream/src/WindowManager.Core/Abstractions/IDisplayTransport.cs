using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Models;

namespace WindowManager.Core.Abstractions;

public interface IDisplayTransport
{
    DisplayTransportKind Kind { get; }

    Task StartAsync(CaptureSession captureSession, DisplayTarget target, CancellationToken cancellationToken);

    Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken);
}
