using System;
using System.Collections.Generic;
using System.Linq;
using WindowManager.Core.Abstractions;
using WindowManager.Core.Models;

namespace WindowManager.App.Runtime;

public sealed class DefaultDisplayTransportResolver : IDisplayTransportResolver
{
    private readonly IReadOnlyDictionary<DisplayTransportKind, IDisplayTransport> _transports;

    public DefaultDisplayTransportResolver(IEnumerable<IDisplayTransport> transports)
    {
        _transports = transports.ToDictionary(x => x.Kind, x => x);
    }

    public IDisplayTransport Resolve(DisplayTransportKind kind)
    {
        if (_transports.TryGetValue(kind, out var transport))
        {
            return transport;
        }

        throw new InvalidOperationException($"Nenhum transporte cadastrado para '{kind}'.");
    }
}
