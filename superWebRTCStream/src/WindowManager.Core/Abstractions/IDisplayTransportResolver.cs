using WindowManager.Core.Models;

namespace WindowManager.Core.Abstractions;

public interface IDisplayTransportResolver
{
    IDisplayTransport Resolve(DisplayTransportKind kind);
}
