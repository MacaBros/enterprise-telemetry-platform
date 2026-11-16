namespace Telemetry.Audio;

/// <summary>
/// Immutable value-type snapshot of audio stream and ring-buffer health for a single processed frame.
/// Declared as a <c>readonly record struct</c> so passing metrics across callbacks/channels creates
/// zero managed heap allocations (no GC pressure on real-time audio hot paths).
/// </summary>
public readonly record struct AudioFrameMetrics(
    string ChannelId,
    int SamplesProcessed,
    double RmsDb,
    double PeakDb,
    bool IsClipping,
    bool IsDeadAir,
    long TotalFramesProcessed,
    long UnderrunCount,
    long OverrunCount,
    double BufferFillRatio,
    DateTimeOffset TimestampUtc
);

