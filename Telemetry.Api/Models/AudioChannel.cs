namespace Telemetry.Api.Models;

/// <summary>
/// Represents a monitored audio/telemetry source (radio dispatch channel, game client, or WCF legacy node).
/// Configured with a 1-to-many relationship to <see cref="StreamIncident"/> in <see cref="Data.TelemetryDb"/>.
/// </summary>
public class AudioChannel
{
    public int Id { get; set; }

    public string ChannelKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string SourceType { get; set; } = "PcmStream";

    public bool IsOnline { get; set; } = true;

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public ICollection<StreamIncident> Incidents { get; set; } = new List<StreamIncident>();
}

