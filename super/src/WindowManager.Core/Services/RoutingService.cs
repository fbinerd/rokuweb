using System;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.Core.Services;

public sealed class RoutingService
{
    private readonly ICaptureSessionFactory _captureSessionFactory;
    private readonly IDisplayTransportResolver _displayTransportResolver;

    public RoutingService(
        ICaptureSessionFactory captureSessionFactory,
        IDisplayTransportResolver displayTransportResolver)
    {
        _captureSessionFactory = captureSessionFactory;
        _displayTransportResolver = displayTransportResolver;
    }

    public async Task<CaptureSession> AssignWindowToTargetAsync(
        WindowSession windowSession,
        DisplayTarget target,
        CancellationToken cancellationToken)
    {
        if (windowSession is null)
        {
            throw new ArgumentNullException(nameof(windowSession));
        }

        if (target is null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (!target.IsOnline)
        {
            throw new InvalidOperationException("O destino selecionado esta offline.");
        }

        if (target.TransportKind == DisplayTransportKind.LanStreaming)
        {
            windowSession.AssignedTarget = target;
            windowSession.State = WindowSessionState.Streaming;
            return new CaptureSession
            {
                WindowSessionId = windowSession.Id,
                IsActive = false
            };
        }

        var captureSession = await _captureSessionFactory.StartAsync(windowSession, cancellationToken);
        var transport = _displayTransportResolver.Resolve(target.TransportKind);

        await transport.StartAsync(captureSession, target, cancellationToken);

        windowSession.AssignedTarget = target;
        windowSession.State = WindowSessionState.Streaming;

        return captureSession;
    }
}
