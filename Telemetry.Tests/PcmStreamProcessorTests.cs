using Telemetry.Audio;
using Xunit;

namespace Telemetry.Tests;

public class PcmStreamProcessorTests
{
    [Fact]
    public async Task WriteChunkAsync_ReassemblesFragmentedChunksAcrossPipeBoundary()
    {
        var processor = new PcmStreamProcessor("test-ch", frameSampleCount: 480, ringBufferCapacitySamples: 2400);
        const int frameBytes = 480 * PcmStreamProcessor.BytesPerSample16Bit; // 960 bytes
        byte[] twoFrames = new byte[frameBytes * 2];
        PcmStreamProcessor.WriteSineWavePcm16(twoFrames, frequencyHz: 440.0, sampleRate: 16000, amplitude: 0.5);

        // Intentionally split on odd byte boundaries (17 bytes, then 942 bytes, then rest)
        await processor.WriteChunkAsync(twoFrames.AsMemory(0, 17));
        var earlyMetrics = new List<AudioFrameMetrics>();
        Assert.False(processor.TryProcessAvailableFrames(earlyMetrics));
        Assert.Empty(earlyMetrics);

        await processor.WriteChunkAsync(twoFrames.AsMemory(17, 942)); // 959 total (1 byte shy of frame!)
        Assert.False(processor.TryProcessAvailableFrames(earlyMetrics));

        await processor.WriteChunkAsync(twoFrames.AsMemory(959)); // Completes both frames
        var completed = new List<AudioFrameMetrics>();
        Assert.True(processor.TryProcessAvailableFrames(completed));
        Assert.Equal(2, completed.Count);

        // 0.5 amplitude sine wave RMS = 0.5 / sqrt(2) ~= 0.3535 -> ~ -9.03 dBFS
        Assert.InRange(completed[0].RmsDb, -9.6, -8.5);
        Assert.False(completed[0].IsClipping);
        Assert.False(completed[0].IsDeadAir);
        Assert.Equal(960, processor.RingBuffer.AvailableToRead);
    }

    [Fact]
    public void ProcessSingleFrame_DetectsDeadAirAndClippingAccurately()
    {
        var processor = new PcmStreamProcessor("test-ch", frameSampleCount: 480, ringBufferCapacitySamples: 2400);
        byte[] frame = new byte[480 * 2];

        // 1. Digital silence -> DeadAir
        AudioFrameMetrics silence = processor.ProcessSingleFrame(frame);
        Assert.True(silence.IsDeadAir);
        Assert.False(silence.IsClipping);
        Assert.True(silence.RmsDb <= PcmStreamProcessor.DeadAirThresholdDb);

        // 2. Full-scale amplitude -> Clipping
        PcmStreamProcessor.WriteSineWavePcm16(frame, frequencyHz: 1000.0, sampleRate: 16000, amplitude: 1.0);
        AudioFrameMetrics hot = processor.ProcessSingleFrame(frame);
        Assert.True(hot.IsClipping);
        Assert.False(hot.IsDeadAir);
    }
}

