using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class StubBrowserInstanceHost : IBrowserInstanceHost
{
    private readonly Dictionary<Guid, WindowSession> _sessions = new Dictionary<Guid, WindowSession>();
    private int _counter;

    public Task<WindowSession> CreateAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        _counter++;

        var session = new WindowSession
        {
            Title = $"Janela {_counter}",
            InitialUri = initialUri,
            NativeHandle = _counter,
            State = WindowSessionState.Running
        };

        _sessions[session.Id] = session;
        return Task.FromResult(session);
    }

    public Task CloseAsync(Guid windowSessionId, CancellationToken cancellationToken)
    {
        _sessions.Remove(windowSessionId);
        return Task.CompletedTask;
    }

    public Task NavigateAsync(Guid windowSessionId, Uri uri, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(windowSessionId, out var session))
        {
            session.InitialUri = uri;
            return Task.CompletedTask;
        }

        _sessions[windowSessionId] = new WindowSession
        {
            Id = windowSessionId,
            Title = "Janela restaurada",
            InitialUri = uri,
            State = WindowSessionState.Running
        };

        return Task.CompletedTask;
    }
}
