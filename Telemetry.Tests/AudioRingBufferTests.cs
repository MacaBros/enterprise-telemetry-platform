using Telemetry.Audio;
using Xunit;

namespace Telemetry.Tests;

public class AudioRingBufferTests
{
    [Fact]
    public void WriteAndRead_WrapsAroundCircularBoundaries_WithoutCorruption()
    {
        var ring = new AudioRingBuffer(capacitySamples: 8);

        // Fill 6 samples, drain 4 -> tail advances to index 4, head at index 6
        ring.Write(new float[] { 1, 2, 3, 4, 5, 6 });
        Span<float> firstRead = stackalloc float[4];
        int readCount1 = ring.Read(firstRead);

        Assert.Equal(4, readCount1);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, firstRead.ToArray());

        // Write 5 more samples (wraps past index 8 back to 0..3)
        ring.Write(new float[] { 7, 8, 9, 10, 11 });
        Assert.Equal(7, ring.AvailableToRead);

        Span<float> secondRead = stackalloc float[7];
        int readCount2 = ring.Read(secondRead);

        Assert.Equal(7, readCount2);
        Assert.Equal(new float[] { 5, 6, 7, 8, 9, 10, 11 }, secondRead.ToArray());
        Assert.Equal(0, ring.UnderrunCount);
        Assert.Equal(0, ring.OverrunCount);
    }

    [Fact]
    public void Read_WhenInsufficientData_ZeroFillsTailAndIncrementsUnderrun()
    {
        var ring = new AudioRingBuffer(capacitySamples: 8);
        ring.Write(new float[] { 0.5f, -0.5f });

        Span<float> dest = new float[] { 99f, 99f, 99f, 99f };
        int actuallyRead = ring.Read(dest);

        Assert.Equal(2, actuallyRead);
        Assert.Equal(1, ring.UnderrunCount);
        Assert.Equal(new float[] { 0.5f, -0.5f, 0f, 0f }, dest.ToArray());
    }

    [Fact]
    public void Write_WhenOverflowingCapacity_DropsOldestSamplesAndIncrementsOverrun()
    {
        var ring = new AudioRingBuffer(capacitySamples: 4);
        ring.Write(new float[] { 1, 2, 3 });
        ring.Write(new float[] { 4, 5, 6 }); // Exceeds capacity 4 by 2 samples

        Assert.Equal(2, ring.OverrunCount);
        Assert.Equal(4, ring.AvailableToRead);

        Span<float> output = stackalloc float[4];
        ring.Read(output);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, output.ToArray());
    }
}

