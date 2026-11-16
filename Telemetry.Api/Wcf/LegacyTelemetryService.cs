using CoreWCF;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Telemetry.Api.Data;
using Telemetry.Api.Hubs;
using Telemetry.Api.Models;

namespace EnterpriseBridge.Wcf;

/// <summary>
/// CoreWCF SOAP service bridging legacy Windows/WCF enterprise clients into the modern
/// .NET 8 EF Core database and SignalR real-time hub.
/// Registered with <see cref="ServiceBehaviorAttribute"/> so DI resolves scoped dependencies cleanly.
/// </summary>
[ServiceBehavior(IncludeExceptionDetailInFaults = true)]
public class LegacyTelemetryService : ILegacyTelemetryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<TelemetryHub, ITelemetryClient> _hub;
    private readonly ILogger<LegacyTelemetryService> _logger;

    public LegacyTelemetryService(
        IServiceScopeFactory scopeFactory,
        IHubContext<TelemetryHub, ITelemetryClient> hub,
        ILogger<LegacyTelemetryService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public async Task<LegacyIngestAck> IngestLegacyTelemetryAsync(LegacyTelemetryPacket packet)
    {
        if (packet is null || string.IsNullOrWhiteSpace(packet.ChannelKey))
        {
            throw new FaultException("ChannelKey is required on LegacyTelemetryPacket.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();

        var now = DateTime.UtcNow;
        var channel = await db.AudioChannels
            .FirstOrDefaultAsync(c => c.ChannelKey == packet.ChannelKey);

        if (channel is null)
        {
            channel = new AudioChannel
            {
                ChannelKey = packet.ChannelKey,
                DisplayName = $"Legacy WCF ({packet.ChannelKey})",
                SourceType = "WcfLegacyBridge",
                IsOnline = true,
                LastSeenUtc = now
            };
            db.AudioChannels.Add(channel);
            await db.SaveChangesAsync();
        }
        else
        {
            channel.LastSeenUtc = now;
            channel.IsOnline = true;
        }

        var telemetryRecord = new TelemetryRecord
        {
            SessionId = Guid.NewGuid(),
            ActivePlayers = packet.ActiveNodes,
            ServerStatus = packet.StatusText,
            ActiveModifiers = $"WCF:{packet.ChannelKey}",
            CreatedAt = now
        };
        db.TelemetryRecords.Add(telemetryRecord);

        var incident = new StreamIncident
        {
            AudioChannelId = channel.Id,
            IncidentType = "LegacyWcfIngest",
            Severity = packet.ReportedRmsDb <= -60 ? "Warning" : "Info",
            RmsDb = packet.ReportedRmsDb,
            Details = $"SOAP ingest from legacy WCF bridge ({packet.StatusText}, {packet.ActiveNodes} active nodes)",
            CreatedAtUtc = now
        };
        db.StreamIncidents.Add(incident);

        await db.SaveChangesAsync();

        // Push both telemetry and WCF bridge incident to all connected SignalR dashboards immediately.
        await _hub.Clients.All.ReceiveTelemetry(telemetryRecord);
        await _hub.Clients.All.ReceiveIncident(new StreamIncidentDto(
            incident.Id,
            channel.ChannelKey,
            channel.DisplayName,
            incident.IncidentType,
            incident.Severity,
            incident.RmsDb,
            incident.Details,
            incident.CreatedAtUtc
        ));

        _logger.LogInformation("CoreWCF ingested legacy packet for {ChannelKey} (RecordId={RecordId})",
            packet.ChannelKey, telemetryRecord.Id);

        return new LegacyIngestAck
        {
            Accepted = true,
            TelemetryRecordId = telemetryRecord.Id,
            Message = $"Bridged SOAP packet into EF Core & SignalR for channel '{packet.ChannelKey}'.",
            ProcessedUtc = now
        };
    }

    public async Task<LegacyChannelStatus> GetChannelStatusAsync(string channelKey)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();

        var channel = await db.AudioChannels
            .AsNoTracking()
            .Where(c => c.ChannelKey == channelKey)
            .Select(c => new
            {
                c.ChannelKey,
                c.IsOnline,
                c.LastSeenUtc,
                IncidentCount = c.Incidents.Count
            })
            .FirstOrDefaultAsync();

        if (channel is null)
        {
            return new LegacyChannelStatus { ChannelKey = channelKey, Found = false };
        }

        return new LegacyChannelStatus
        {
            ChannelKey = channel.ChannelKey,
            Found = true,
            IsOnline = channel.IsOnline,
            IncidentCount = channel.IncidentCount,
            LastSeenUtc = channel.LastSeenUtc
        };
    }
}
