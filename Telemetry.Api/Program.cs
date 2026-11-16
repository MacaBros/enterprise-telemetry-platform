using System.Buffers;
using System.Text;
using CoreWCF;
using CoreWCF.Configuration;
using CoreWCF.Description;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Telemetry.Api.Data;
using Telemetry.Api.Hubs;
using Telemetry.Api.Models;
using Telemetry.Api.Services;
using EnterpriseBridge.Wcf;
using Telemetry.Audio;

var builder = WebApplication.CreateBuilder(args);

// 1. CORS configured with AllowCredentials() so SignalR WebSockets work cleanly from Vite dev server
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

// 2. Entity Framework Core (Scoped DbContext per request)
builder.Services.AddDbContext<TelemetryDb>(options =>
    options.UseSqlite("Data Source=telemetry.db"));

// 3. SignalR real-time hub + low-level audio telemetry engine
builder.Services.AddSignalR();
builder.Services.AddSingleton<AudioTelemetryEngine>();
builder.Services.AddHostedService<AudioStreamSimulatorService>();

// 4. CoreWCF legacy SOAP integration services
builder.Services.AddServiceModelServices();
builder.Services.AddServiceModelMetadata();
builder.Services.AddTransient<LegacyTelemetryService>();

var app = builder.Build();

// Ensure relational tables exist on startup (safe against pre-existing telemetry.db)
using (var startupScope = app.Services.CreateScope())
{
    var db = startupScope.ServiceProvider.GetRequiredService<TelemetryDb>();
    await db.EnsureSchemaInitializedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowFrontend");

// Map SignalR Hub
app.MapHub<TelemetryHub>("/hubs/telemetry");

// Map CoreWCF SOAP 1.1 Endpoint + WSDL metadata
app.UseServiceModel(serviceBuilder =>
{
    serviceBuilder.AddService<LegacyTelemetryService>();
    serviceBuilder.AddServiceEndpoint<LegacyTelemetryService, ILegacyTelemetryService>(
        new BasicHttpBinding(),
        "/soap/LegacyTelemetryService.svc");

    var metadataBehavior = app.Services.GetRequiredService<ServiceMetadataBehavior>();
    metadataBehavior.HttpGetEnabled = true;
});

// ---------------- TELEMETRY & AUDIO API ENDPOINTS ----------------

app.MapPost("/api/telemetry", async (
    TelemetryRecord incomingData,
    TelemetryDb db,
    IHubContext<TelemetryHub, ITelemetryClient> hub) =>
{
    if (incomingData.CreatedAt == default)
        incomingData.CreatedAt = DateTime.UtcNow;

    db.TelemetryRecords.Add(incomingData);
    await db.SaveChangesAsync();

    // Real-time SignalR broadcast replaces client-side HTTP polling
    await hub.Clients.All.ReceiveTelemetry(incomingData);

    return Results.Created($"/api/telemetry/{incomingData.Id}", incomingData);
})
.WithName("SubmitTelemetry")
.WithOpenApi();

app.MapGet("/api/telemetry", async (TelemetryDb db) =>
{
    var historicalData = await db.TelemetryRecords
        .AsNoTracking()
        .OrderByDescending(t => t.CreatedAt)
        .Take(15)
        .ToListAsync();

    historicalData.Reverse();
    return Results.Ok(historicalData);
})
.WithName("GetTelemetry")
.WithOpenApi();

app.MapGet("/api/incidents", async (TelemetryDb db) =>
{
    var incidents = await db.StreamIncidents
        .AsNoTracking()
        .Include(i => i.AudioChannel)
        .OrderByDescending(i => i.CreatedAtUtc)
        .Take(25)
        .Select(i => new StreamIncidentDto(
            i.Id,
            i.AudioChannel != null ? i.AudioChannel.ChannelKey : "unknown",
            i.AudioChannel != null ? i.AudioChannel.DisplayName : "Unknown Channel",
            i.IncidentType,
            i.Severity,
            i.RmsDb,
            i.Details,
            i.CreatedAtUtc))
        .ToListAsync();

    return Results.Ok(incidents);
})
.WithName("GetStreamIncidents")
.WithOpenApi();

app.MapGet("/api/audio/mode", (AudioTelemetryEngine engine) =>
    Results.Ok(new { mode = engine.SimulationMode.ToString() }))
.WithName("GetAudioSimulationMode")
.WithOpenApi();

app.MapPost("/api/audio/mode/{mode}", (string mode, AudioTelemetryEngine engine) =>
{
    if (!Enum.TryParse<StreamSimulationMode>(mode, ignoreCase: true, out var parsed))
    {
        return Results.BadRequest(new
        {
            error = $"Invalid mode '{mode}'. Allowed: {string.Join(", ", Enum.GetNames<StreamSimulationMode>())}"
        });
    }

    engine.SimulationMode = parsed;
    return Results.Ok(new { mode = engine.SimulationMode.ToString() });
})
.WithName("SetAudioSimulationMode")
.WithOpenApi();

app.MapPost("/api/audio/ingest/{channelKey}", async (
    string channelKey,
    AudioIngestRequest request,
    AudioTelemetryEngine engine,
    CancellationToken ct) =>
{
    const int frameBytes = 480 * PcmStreamProcessor.BytesPerSample16Bit;
    int chunkCount = Math.Clamp(request.Frames, 1, 20);
    int totalBytes = frameBytes * chunkCount;

    byte[] rented = ArrayPool<byte>.Shared.Rent(totalBytes);
    try
    {
        FillPcmPayload(rented, totalBytes, request.FrequencyHz, request.Amplitude);

        // Feed split across non-aligned chunks to exercise PipeReader partial frame reassembly
        int splitPoint = Math.Max(1, totalBytes / 3);
        await engine.IngestPcm16Async(channelKey, rented.AsMemory(0, splitPoint), simulateConsumerDrain: false, ct);
        var metrics = await engine.IngestPcm16Async(channelKey, rented.AsMemory(splitPoint, totalBytes - splitPoint), simulateConsumerDrain: true, ct);

        return Results.Ok(new
        {
            channelKey,
            framesProcessed = metrics.Count,
            latest = metrics.Count > 0 ? metrics[^1] : (AudioFrameMetrics?)null
        });
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rented);
    }
})
.WithName("IngestSyntheticPcmFrames")
.WithOpenApi();

// Fires a real SOAP 1.1 envelope over HTTP into CoreWCF so the React UI can exercise WCF end-to-end
app.MapPost("/api/legacy/soap-demo", async (
    LegacyDemoRequest? req,
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory) =>
{
    string channelKey = string.IsNullOrWhiteSpace(req?.ChannelKey) ? "wcf-dispatch-01" : req.ChannelKey;
    int activeNodes = req?.ActiveNodes ?? 42;
    string statusText = string.IsNullOrWhiteSpace(req?.StatusText) ? "WCF_DISPATCH_NOMINAL" : req.StatusText;
    double rmsDb = req?.ReportedRmsDb ?? -14.5;

    string soapEnvelope = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <IngestLegacyTelemetry xmlns="http://macabros.dev/telemetry/legacy/2026">
              <packet xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                <ChannelKey>{System.Security.SecurityElement.Escape(channelKey)}</ChannelKey>
                <ActiveNodes>{activeNodes}</ActiveNodes>
                <StatusText>{System.Security.SecurityElement.Escape(statusText)}</StatusText>
                <ReportedRmsDb>{rmsDb}</ReportedRmsDb>
              </packet>
            </IngestLegacyTelemetry>
          </s:Body>
        </s:Envelope>
        """;

    string targetUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/soap/LegacyTelemetryService.svc";
    using var client = httpClientFactory.CreateClient();
    using var httpMsg = new HttpRequestMessage(HttpMethod.Post, targetUrl);
    httpMsg.Headers.Add("SOAPAction", "\"http://macabros.dev/telemetry/legacy/2026/ILegacyTelemetryService/IngestLegacyTelemetry\"");
    httpMsg.Content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");

    using var resp = await client.SendAsync(httpMsg);
    string responseXml = await resp.Content.ReadAsStringAsync();

    return Results.Ok(new
    {
        endpoint = targetUrl,
        statusCode = (int)resp.StatusCode,
        soapRequest = soapEnvelope,
        soapResponse = responseXml
    });
})
.WithName("TriggerCoreWcfSoapDemo")
.WithOpenApi();

app.Run();

static void FillPcmPayload(byte[] buffer, int length, double frequencyHz, double amplitude)
{
    Span<byte> payload = buffer.AsSpan(0, length);
    if (amplitude <= 0.0001)
    {
        payload.Clear();
    }
    else
    {
        PcmStreamProcessor.WriteSineWavePcm16(
            payload,
            frequencyHz: frequencyHz,
            sampleRate: 16000,
            amplitude: amplitude);
    }
}

public sealed record AudioIngestRequest(double FrequencyHz = 440.0, double Amplitude = 0.5, int Frames = 2);
public sealed record LegacyDemoRequest(string? ChannelKey, int? ActiveNodes, string? StatusText, double? ReportedRmsDb);
