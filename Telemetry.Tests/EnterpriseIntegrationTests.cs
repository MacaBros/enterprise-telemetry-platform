using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telemetry.Api.Data;
using Telemetry.Api.Hubs;
using Telemetry.Api.Models;
using EnterpriseBridge.Wcf;
using Telemetry.Audio;
using Xunit;

namespace Telemetry.Tests;

public class EnterpriseIntegrationTests
{
    [Fact]
    public async Task LegacyWcfService_PersistsToEfCore_AndBroadcastsViaSignalR()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<TelemetryDb>(opts => opts.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        using (var initScope = provider.CreateScope())
        {
            var db = initScope.ServiceProvider.GetRequiredService<TelemetryDb>();
            await db.Database.EnsureCreatedAsync();
        }

        var fakeHub = new FakeTelemetryHubContext();
        var wcfService = new LegacyTelemetryService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fakeHub,
            NullLogger<LegacyTelemetryService>.Instance);

        var ack = await wcfService.IngestLegacyTelemetryAsync(new LegacyTelemetryPacket
        {
            ChannelKey = "wcf-test-ch",
            ActiveNodes = 17,
            StatusText = "NOMINAL",
            ReportedRmsDb = -16.2
        });

        Assert.True(ack.Accepted);
        Assert.True(ack.TelemetryRecordId > 0);
        Assert.Single(fakeHub.FakeClients.SentTelemetry);
        Assert.Single(fakeHub.FakeClients.SentIncidents);

        var status = await wcfService.GetChannelStatusAsync("wcf-test-ch");
        Assert.True(status.Found);
        Assert.True(status.IsOnline);
        Assert.Equal(1, status.IncidentCount);
    }

    private sealed class FakeTelemetryHubContext : IHubContext<TelemetryHub, ITelemetryClient>
    {
        public RecordingTelemetryClient FakeClients { get; } = new();
        public IHubClients<ITelemetryClient> Clients => FakeClients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class RecordingTelemetryClient : IHubClients<ITelemetryClient>, ITelemetryClient
    {
        public List<TelemetryRecord> SentTelemetry { get; } = new();
        public List<StreamIncidentDto> SentIncidents { get; } = new();
        public List<AudioFrameMetrics> SentAudio { get; } = new();

        public ITelemetryClient All => this;
        public ITelemetryClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => this;
        public ITelemetryClient Client(string connectionId) => this;
        public ITelemetryClient Clients(IReadOnlyList<string> connectionIds) => this;
        public ITelemetryClient Group(string groupName) => this;
        public ITelemetryClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => this;
        public ITelemetryClient Groups(IReadOnlyList<string> groupNames) => this;
        public ITelemetryClient User(string userId) => this;
        public ITelemetryClient Users(IReadOnlyList<string> userIds) => this;

        public Task ReceiveTelemetry(TelemetryRecord record)
        {
            SentTelemetry.Add(record);
            return Task.CompletedTask;
        }

        public Task ReceiveAudioMetrics(AudioFrameMetrics metrics)
        {
            SentAudio.Add(metrics);
            return Task.CompletedTask;
        }

        public Task ReceiveIncident(StreamIncidentDto incident)
        {
            SentIncidents.Add(incident);
            return Task.CompletedTask;
        }
    }
}
