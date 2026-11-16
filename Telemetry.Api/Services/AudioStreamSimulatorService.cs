using System.Buffers;
using Telemetry.Audio;

namespace Telemetry.Api.Services;

/// <summary>
/// Background hosted service generating continuous synthetic 16-bit PCM frames
/// (Normal Voice, Dead Air, Clipping Spike, or Jitter Underrun) through <see cref="AudioTelemetryEngine"/>
/// using pooled byte buffers so SignalR dashboards display live audio health immediately on startup.
/// </summary>
public sealed class AudioStreamSimulatorService : BackgroundService
{
    private const string DefaultChannel = "dispatch-ch1";
    private readonly AudioTelemetryEngine _engine;
    private readonly ILogger<AudioStreamSimulatorService> _logger;

    public AudioStreamSimulatorService(AudioTelemetryEngine engine, ILogger<AudioStreamSimulatorService> logger)
    {
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AudioStreamSimulatorService started for channel {Channel}.", DefaultChannel);

        const int frameBytes = 480 * PcmStreamProcessor.BytesPerSample16Bit; // 960 bytes (30ms @ 16kHz)
        double phaseSeconds = 0.0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StreamSimulationMode mode = _engine.SimulationMode;
                if (mode == StreamSimulationMode.JitterUnderrun)
                {
                    await _engine.ForceUnderrunTickAsync(DefaultChannel, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(frameBytes);
                    try
                    {
                        // Span<T> is a ref struct and stays in synchronous helper; Memory<T> safely crosses await.
                        FillSimulatedFrame(rented, frameBytes, mode, phaseSeconds);

                        await _engine.IngestPcm16Async(
                            DefaultChannel,
                            rented.AsMemory(0, frameBytes),
                            simulateConsumerDrain: true,
                            ct: stoppingToken,
                            isPttKeyed: mode != StreamSimulationMode.PttIdle).ConfigureAwait(false);

                        phaseSeconds += 0.03;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AudioStreamSimulatorService tick encountered an error.");
            }

            await Task.Delay(500, stoppingToken).ConfigureAwait(false);
        }
    }

    private static void FillSimulatedFrame(byte[] buffer, int length, StreamSimulationMode mode, double phaseSeconds)
    {
        Span<byte> chunk = buffer.AsSpan(0, length);
        switch (mode)
        {
            case StreamSimulationMode.PttIdle:
            case StreamSimulationMode.DeadAir:
                chunk.Clear();
                break;
            case StreamSimulationMode.ClippingSpike:
                PcmStreamProcessor.WriteSineWavePcm16(chunk, frequencyHz: 880.0, sampleRate: 16000, amplitude: 1.0, phaseOffsetSeconds: phaseSeconds);
                break;
            default:
                double amp = 0.25 + 0.12 * Math.Sin(phaseSeconds * 2.5);
                PcmStreamProcessor.WriteSineWavePcm16(chunk, frequencyHz: 340.0, sampleRate: 16000, amplitude: amp, phaseOffsetSeconds: phaseSeconds);
                break;
        }
    }
}
