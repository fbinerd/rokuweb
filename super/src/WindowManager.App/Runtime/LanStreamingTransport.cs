using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class LanStreamingTransport : IDisplayTransport
{
    public DisplayTransportKind Kind => DisplayTransportKind.LanStreaming;

    public Task StartAsync(CaptureSession captureSession, DisplayTarget target, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
