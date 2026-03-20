using System;

namespace WindowManager.App.Runtime.Publishing;

public sealed class PublishedWindowRoute
{
    public Guid WindowId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string RoutePath { get; set; } = string.Empty;

    public int Port { get; set; }

    public string PublishedUrl { get; set; } = string.Empty;
}
