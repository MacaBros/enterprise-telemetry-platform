using System;

namespace Telemetry.Api.Models;

public class TelemetryRecord
{
    public int Id { get; set; }

    public Guid SessionId { get; set; }

    public int ActivePlayers { get; set; }

    public string ServerStatus { get; set; } = string.Empty;

    public string ActiveModifiers { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}