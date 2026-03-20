using System;

namespace WindowManager.Core.Models;

public sealed class CaptureSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WindowSessionId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsActive { get; set; }
}

