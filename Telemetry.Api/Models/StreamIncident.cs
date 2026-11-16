namespace Telemetry.Api.Models;

/// <summary>
/// Persisted audio buffer anomaly or legacy ingest event linked to an <see cref="AudioChannel"/>.
/// </summary>
public class StreamIncident
{
    public int Id { get; set; }

    public int AudioChannelId { get; set; }

    public AudioChannel? AudioChannel { get; set; }

    public string IncidentType { get; set; } = string.Empty;

    public string Severity { get; set; } = "Info";

    public double RmsDb { get; set; }

    public string Details { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record StreamIncidentDto(
    int Id,
    string ChannelKey,
    string ChannelName,
    string IncidentType,
    string Severity,
    double RmsDb,
    string Details,
    DateTime CreatedAtUtc
);

