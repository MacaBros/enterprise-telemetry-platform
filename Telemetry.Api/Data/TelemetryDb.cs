using Microsoft.EntityFrameworkCore;
using Telemetry.Api.Models;

namespace Telemetry.Api.Data;

/// <summary>
/// EF Core database context managing telemetry snapshots, monitored audio channels, and incident history.
/// Registered as a Scoped service per HTTP/SignalR request or background scope.
/// </summary>
public class TelemetryDb : DbContext
{
    public TelemetryDb(DbContextOptions<TelemetryDb> options) : base(options) { }

    public DbSet<TelemetryRecord> TelemetryRecords => Set<TelemetryRecord>();
    public DbSet<AudioChannel> AudioChannels => Set<AudioChannel>();
    public DbSet<StreamIncident> StreamIncidents => Set<StreamIncident>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AudioChannel>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.ChannelKey).HasMaxLength(64).IsRequired();
            entity.Property(c => c.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(c => c.SourceType).HasMaxLength(32).IsRequired();
            entity.HasIndex(c => c.ChannelKey).IsUnique();
        });

        modelBuilder.Entity<StreamIncident>(entity =>
        {
            entity.HasKey(i => i.Id);
            entity.Property(i => i.IncidentType).HasMaxLength(48).IsRequired();
            entity.Property(i => i.Severity).HasMaxLength(16).IsRequired();
            entity.Property(i => i.Details).HasMaxLength(512);
            entity.HasIndex(i => new { i.AudioChannelId, i.CreatedAtUtc });

            entity.HasOne(i => i.AudioChannel)
                  .WithMany(c => c.Incidents)
                  .HasForeignKey(i => i.AudioChannelId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Ensures relational audio tables exist even against pre-existing SQLite telemetry.db files.
    /// </summary>
    public async Task EnsureSchemaInitializedAsync(CancellationToken ct = default)
    {
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TelemetryRecords" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TelemetryRecords" PRIMARY KEY AUTOINCREMENT,
                "SessionId" TEXT NOT NULL,
                "ActivePlayers" INTEGER NOT NULL,
                "ServerStatus" TEXT NOT NULL,
                "ActiveModifiers" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "AudioChannels" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AudioChannels" PRIMARY KEY AUTOINCREMENT,
                "ChannelKey" TEXT NOT NULL,
                "DisplayName" TEXT NOT NULL,
                "SourceType" TEXT NOT NULL,
                "IsOnline" INTEGER NOT NULL,
                "LastSeenUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_AudioChannels_ChannelKey" ON "AudioChannels" ("ChannelKey");
            CREATE TABLE IF NOT EXISTS "StreamIncidents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_StreamIncidents" PRIMARY KEY AUTOINCREMENT,
                "AudioChannelId" INTEGER NOT NULL,
                "IncidentType" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "RmsDb" REAL NOT NULL,
                "Details" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_StreamIncidents_AudioChannels_AudioChannelId" FOREIGN KEY ("AudioChannelId") REFERENCES "AudioChannels" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_StreamIncidents_AudioChannelId_CreatedAtUtc" ON "StreamIncidents" ("AudioChannelId", "CreatedAtUtc");
            """, ct);
    }
}
