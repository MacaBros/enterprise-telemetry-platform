using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Telemetry.Api.Data;
using Telemetry.Api.Hubs;
using Telemetry.Api.Models;
using Telemetry.Audio;

namespace Telemetry.Api.Services;

public enum StreamSimulationMode
{
    NormalVoice,
    PttIdle,
    DeadAir,
    ClippingSpike,
    JitterUnderrun
}

/// <summary>
/// Central coordinator holding per-channel <see cref="PcmStreamProcessor"/> instances,
/// handling incoming binary PCM chunks, consuming frames via stack-allocated buffers,
/// pushing live metrics over SignalR, and persisting threshold anomalies via EF Core.
/// </summary>
public sealed class AudioTelemetryEngine
{
    private readonly ConcurrentDictionary<string, PcmStreamProcessor> _processors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastIncidentAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<TelemetryHub, ITelemetryClient> _hub;
    private readonly ILogger<AudioTelemetryEngine> _logger;

    private volatile StreamSimulationMode _simulationMode = StreamSimulationMode.NormalVoice;

    public StreamSimulationMode SimulationMode
    {
        get => _simulationMode;
        set => _simulationMode = value;
    }

    public AudioTelemetryEngine(
        IServiceScopeFactory scopeFactory,
        IHubContext<TelemetryHub, ITelemetryClient> hub,
        ILogger<AudioTelemetryEngine> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public PcmStreamProcessor GetOrCreateProcessor(string channelKey)
        => _processors.GetOrAdd(channelKey, key => new PcmStreamProcessor(key, frameSampleCount: 480, ringBufferCapacitySamples: 4800));

    public async Task<IReadOnlyList<AudioFrameMetrics>> IngestPcm16Async(
        string channelKey,
        ReadOnlyMemory<byte> pcm16Bytes,
        bool simulateConsumerDrain = true,
        CancellationToken ct = default,
        bool isPttKeyed = true)
    {
        var processor = GetOrCreateProcessor(channelKey);
        await processor.WriteChunkAsync(pcm16Bytes, ct).ConfigureAwait(false);

        var metricsList = new List<AudioFrameMetrics>(4);
        processor.TryProcessAvailableFrames(metricsList);

        if (simulateConsumerDrain && metricsList.Count > 0)
        {
            ConsumeFromRingBufferZeroAlloc(processor.RingBuffer, processor.FrameSampleCount * metricsList.Count);
        }

        if (metricsList.Count > 0)
        {
            AudioFrameMetrics latest = metricsList[^1];
            await _hub.Clients.All.ReceiveAudioMetrics(latest).ConfigureAwait(false);
            // When Push-to-Talk (PTT) is released/idle between two-way transmissions, silence is expected and should not trigger a DeadAir alarm.
            if (isPttKeyed || latest.IsClipping)
            {
                await CheckAndPersistIncidentAsync(latest, ct).ConfigureAwait(false);
            }
        }

        return metricsList;
    }

    public async Task<AudioFrameMetrics> ForceUnderrunTickAsync(string channelKey, CancellationToken ct = default)
    {
        var processor = GetOrCreateProcessor(channelKey);
        // Drain twice the frame size to intentionally empty the ring buffer and trigger an underrun event.
        ConsumeFromRingBufferZeroAlloc(processor.RingBuffer, processor.RingBuffer.Capacity + processor.FrameSampleCount);

        var metrics = new AudioFrameMetrics(
            ChannelId: channelKey,
            SamplesProcessed: 0,
            RmsDb: -96.0,
            PeakDb: -96.0,
            IsClipping: false,
            IsDeadAir: true,
            TotalFramesProcessed: processor.TotalFramesProcessed,
            UnderrunCount: processor.RingBuffer.UnderrunCount,
            OverrunCount: processor.RingBuffer.OverrunCount,
            BufferFillRatio: Math.Round(processor.RingBuffer.GetFillRatio(), 3),
            TimestampUtc: DateTimeOffset.UtcNow
        );

        await _hub.Clients.All.ReceiveAudioMetrics(metrics).ConfigureAwait(false);
        await PersistIncidentAsync(channelKey, "BufferUnderrun", "Critical", metrics.RmsDb,
            $"Audio ring buffer starved (total underruns: {metrics.UnderrunCount}); filled tail with silence.", ct).ConfigureAwait(false);
        return metrics;
    }

    private static void ConsumeFromRingBufferZeroAlloc(AudioRingBuffer ringBuffer, int samplesToDrain)
    {
        // Stack-allocate a small scratch window (480 floats = 1.92 KB) so consumer playback causes zero GC churn.
        Span<float> stackWindow = stackalloc float[480];
        int remaining = samplesToDrain;
        while (remaining > 0)
        {
            int sliceLen = Math.Min(remaining, stackWindow.Length);
            ringBuffer.Read(stackWindow[..sliceLen]);
            remaining -= sliceLen;
        }
    }

    private async Task CheckAndPersistIncidentAsync(AudioFrameMetrics metrics, CancellationToken ct)
    {
        if (!metrics.IsClipping && !metrics.IsDeadAir)
            return;

        string incidentType = metrics.IsClipping ? "Clipping" : "DeadAir";
        string severity = metrics.IsClipping ? "Warning" : "Critical";
        string details = metrics.IsClipping
            ? $"Peak exceeded clipping threshold ({metrics.PeakDb} dBFS)."
            : $"Stream signal dropped below dead-air floor ({metrics.RmsDb} dBFS).";

        string cooldownKey = $"{metrics.ChannelId}:{incidentType}";
        var now = DateTimeOffset.UtcNow;
        if (_lastIncidentAt.TryGetValue(cooldownKey, out DateTimeOffset last) && (now - last).TotalSeconds < 4)
        {
            return;
        }

        _lastIncidentAt[cooldownKey] = now;
        await PersistIncidentAsync(metrics.ChannelId, incidentType, severity, metrics.RmsDb, details, ct).ConfigureAwait(false);
    }

    public async Task PersistIncidentAsync(
        string channelKey,
        string incidentType,
        string severity,
        double rmsDb,
        string details,
        CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDb>();

        var now = DateTime.UtcNow;
        var channel = await db.AudioChannels.FirstOrDefaultAsync(c => c.ChannelKey == channelKey, ct);
        if (channel is null)
        {
            channel = new AudioChannel
            {
                ChannelKey = channelKey,
                DisplayName = channelKey == "dispatch-ch1" ? "Dispatch Primary Radio (CH1)" : channelKey,
                SourceType = "PcmStream",
                IsOnline = true,
                LastSeenUtc = now
            };
            db.AudioChannels.Add(channel);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            channel.LastSeenUtc = now;
            channel.IsOnline = true;
        }

        var incident = new StreamIncident
        {
            AudioChannelId = channel.Id,
            IncidentType = incidentType,
            Severity = severity,
            RmsDb = rmsDb,
            Details = details,
            CreatedAtUtc = now
        };

        db.StreamIncidents.Add(incident);
        await db.SaveChangesAsync(ct);

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

        _logger.LogInformation("Logged {Severity} {IncidentType} on {ChannelKey}: {Details}",
            severity, incidentType, channelKey, details);
    }
}

