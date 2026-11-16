using Microsoft.AspNetCore.SignalR;
using Telemetry.Api.Models;
using Telemetry.Audio;

namespace Telemetry.Api.Hubs;

/// <summary>
/// Strongly-typed SignalR client contract defining server-to-browser push methods.
/// Eliminates magic string method names on the C# server.
/// </summary>
public interface ITelemetryClient
{
    Task ReceiveTelemetry(TelemetryRecord record);
    Task ReceiveAudioMetrics(AudioFrameMetrics metrics);
    Task ReceiveIncident(StreamIncidentDto incident);
}

/// <summary>
/// Real-time SignalR Hub pushing telemetry, zero-alloc audio buffer health metrics,
/// and EF Core stream incidents directly to connected clients over WebSockets (with automatic SSE/LongPolling fallback).
/// </summary>
public class TelemetryHub : Hub<ITelemetryClient>
{
    private readonly ILogger<TelemetryHub> _logger;

    public TelemetryHub(ILogger<TelemetryHub> logger)
    {
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR client disconnected: {ConnectionId} ({Error})",
            Context.ConnectionId, exception?.Message ?? "clean");
        return base.OnDisconnectedAsync(exception);
    }

    public Task JoinChannelGroup(string channelKey)
        => Groups.AddToGroupAsync(Context.ConnectionId, channelKey);

    public Task LeaveChannelGroup(string channelKey)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, channelKey);
}

