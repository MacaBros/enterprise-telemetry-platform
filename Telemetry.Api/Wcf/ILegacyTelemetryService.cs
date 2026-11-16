using System.Runtime.Serialization;
using CoreWCF;

namespace EnterpriseBridge.Wcf;

[ServiceContract(Namespace = "http://macabros.dev/telemetry/legacy/2026")]
public interface ILegacyTelemetryService
{
    [OperationContract]
    Task<LegacyIngestAck> IngestLegacyTelemetryAsync(LegacyTelemetryPacket packet);

    [OperationContract]
    Task<LegacyChannelStatus> GetChannelStatusAsync(string channelKey);
}

[DataContract(Namespace = "http://macabros.dev/telemetry/legacy/2026")]
public class LegacyTelemetryPacket
{
    [DataMember(Order = 1)]
    public string ChannelKey { get; set; } = "dispatch-ch1";

    [DataMember(Order = 2)]
    public int ActiveNodes { get; set; }

    [DataMember(Order = 3)]
    public string StatusText { get; set; } = "OK";

    [DataMember(Order = 4)]
    public double ReportedRmsDb { get; set; } = -18.0;
}

[DataContract(Namespace = "http://macabros.dev/telemetry/legacy/2026")]
public class LegacyIngestAck
{
    [DataMember(Order = 1)]
    public bool Accepted { get; set; }

    [DataMember(Order = 2)]
    public int TelemetryRecordId { get; set; }

    [DataMember(Order = 3)]
    public string Message { get; set; } = string.Empty;

    [DataMember(Order = 4)]
    public DateTime ProcessedUtc { get; set; }
}

[DataContract(Namespace = "http://macabros.dev/telemetry/legacy/2026")]
public class LegacyChannelStatus
{
    [DataMember(Order = 1)]
    public string ChannelKey { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public bool Found { get; set; }

    [DataMember(Order = 3)]
    public bool IsOnline { get; set; }

    [DataMember(Order = 4)]
    public int IncidentCount { get; set; }

    [DataMember(Order = 5)]
    public DateTime? LastSeenUtc { get; set; }
}
