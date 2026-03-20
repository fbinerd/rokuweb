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
        AppLog.Write(
            "Transporte",
            string.Format(
                "LanStreaming acionado para '{0}' ({1}), mas a transmissao real ainda nao foi implementada.",
                target.Name,
                target.NetworkAddress));
        return Task.CompletedTask;
    }

    public Task StopAsync(CaptureSession captureSession, CancellationToken cancellationToken)
    {
        AppLog.Write("Transporte", "LanStreaming encerrado.");
        return Task.CompletedTask;
    }
}
