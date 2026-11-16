using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace Telemetry.Audio;

/// <summary>
/// High-throughput, low-allocation 16-bit PCM stream framer and signal analyzer.
/// Bridges chunked network/hardware byte streams (<see cref="Pipe"/> / <see cref="ReadOnlySequence{T}"/>)
/// into fixed-duration audio frames using <see cref="ArrayPool{T}"/>, <see cref="Span{T}"/>,
/// <see cref="BinaryPrimitives"/>, and <see cref="AudioRingBuffer"/>.
/// </summary>
public sealed class PcmStreamProcessor
{
    public const int BytesPerSample16Bit = 2;
    public const double ClippingThresholdLinear = 0.98;
    public const double DeadAirThresholdDb = -60.0;
    private const double MinDbFloor = -96.0;

    private readonly Pipe _pipe = new();
    private readonly AudioRingBuffer _ringBuffer;
    private readonly string _channelId;
    private readonly int _frameSampleCount;
    private readonly int _frameByteCount;
    private long _totalFramesProcessed;

    public AudioRingBuffer RingBuffer => _ringBuffer;
    public int FrameSampleCount => _frameSampleCount;
    public long TotalFramesProcessed => Interlocked.Read(ref _totalFramesProcessed);

    public PcmStreamProcessor(string channelId, int frameSampleCount = 480, int ringBufferCapacitySamples = 4800)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            throw new ArgumentException("Channel ID is required.", nameof(channelId));
        if (frameSampleCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSampleCount));

        _channelId = channelId;
        _frameSampleCount = frameSampleCount;
        _frameByteCount = frameSampleCount * BytesPerSample16Bit;
        _ringBuffer = new AudioRingBuffer(ringBufferCapacitySamples);
    }

    /// <summary>
    /// Enqueues raw incoming network/audio bytes directly into the <see cref="PipeWriter"/> memory,
    /// avoiding extra intermediate buffer allocations when chunks arrive split across TCP/UDP boundaries.
    /// </summary>
    public async ValueTask WriteChunkAsync(ReadOnlyMemory<byte> pcm16Chunk, CancellationToken cancellationToken = default)
    {
        if (pcm16Chunk.IsEmpty)
            return;

        Memory<byte> pipeMemory = _pipe.Writer.GetMemory(pcm16Chunk.Length);
        pcm16Chunk.CopyTo(pipeMemory);
        _pipe.Writer.Advance(pcm16Chunk.Length);
        await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the input pipe completed (no further chunks will arrive).
    /// </summary>
    public ValueTask CompleteInputAsync() => _pipe.Writer.CompleteAsync();

    /// <summary>
    /// Drains all complete PCM frames currently buffered in the <see cref="PipeReader"/>,
    /// parses them with pooled temporary buffers, updates the <see cref="AudioRingBuffer"/>,
    /// and returns per-frame signal health metrics.
    /// </summary>
    public bool TryProcessAvailableFrames(IList<AudioFrameMetrics> metricsOut)
    {
        if (!_pipe.Reader.TryRead(out ReadResult result))
            return false;

        ReadOnlySequence<byte> buffer = result.Buffer;
        bool processedAny = false;

        // Rent pooled scratch buffers once for the batch instead of allocating per frame on the GC heap.
        byte[] pooledBytes = ArrayPool<byte>.Shared.Rent(_frameByteCount);
        float[] pooledSamples = ArrayPool<float>.Shared.Rent(_frameSampleCount);

        try
        {
            Span<byte> frameBytes = pooledBytes.AsSpan(0, _frameByteCount);
            Span<float> frameSamples = pooledSamples.AsSpan(0, _frameSampleCount);

            while (buffer.Length >= _frameByteCount)
            {
                ReadOnlySequence<byte> frameSlice = buffer.Slice(0, _frameByteCount);

                // Fast path: if the slice lives in one contiguous memory segment, decode directly without copying.
                if (frameSlice.IsSingleSegment)
                {
                    DecodeAndAnalyzeFrame(frameSlice.FirstSpan, frameSamples, out AudioFrameMetrics metrics);
                    metricsOut.Add(metrics);
                }
                else
                {
                    // Multi-segment ReadOnlySequence boundary: copy only this frame's bytes into rented scratch memory.
                    frameSlice.CopyTo(frameBytes);
                    DecodeAndAnalyzeFrame(frameBytes, frameSamples, out AudioFrameMetrics metrics);
                    metricsOut.Add(metrics);
                }

                buffer = buffer.Slice(_frameByteCount);
                processedAny = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooledBytes);
            ArrayPool<float>.Shared.Return(pooledSamples);
            // Tell PipeReader exactly how far we consumed so remaining partial bytes stay for the next chunk.
            _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
        }

        return processedAny;
    }

    /// <summary>
    /// Convenience method for synchronous callers holding a complete span of PCM16 bytes:
    /// decodes, meters, and pushes directly into the ring buffer with zero heap allocation.
    /// </summary>
    public AudioFrameMetrics ProcessSingleFrame(ReadOnlySpan<byte> pcm16Frame)
    {
        if (pcm16Frame.Length < _frameByteCount)
            throw new ArgumentException($"Expected at least {_frameByteCount} bytes.", nameof(pcm16Frame));

        float[] pooledSamples = ArrayPool<float>.Shared.Rent(_frameSampleCount);
        try
        {
            Span<float> samples = pooledSamples.AsSpan(0, _frameSampleCount);
            DecodeAndAnalyzeFrame(pcm16Frame[.._frameByteCount], samples, out AudioFrameMetrics metrics);
            return metrics;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooledSamples);
        }
    }

    private void DecodeAndAnalyzeFrame(ReadOnlySpan<byte> frameBytes, Span<float> samples, out AudioFrameMetrics metrics)
    {
        double sumSquares = 0.0;
        double peakAbs = 0.0;

        // Fast-path little-endian view when running on LE architecture (x64/ARM64 Windows/Linux).
        if (BitConverter.IsLittleEndian)
        {
            ReadOnlySpan<short> pcm16 = MemoryMarshal.Cast<byte, short>(frameBytes);
            for (int i = 0; i < _frameSampleCount; i++)
            {
                float normalized = pcm16[i] / 32768f;
                samples[i] = normalized;

                double abs = Math.Abs(normalized);
                if (abs > peakAbs) peakAbs = abs;
                sumSquares += normalized * normalized;
            }
        }
        else
        {
            for (int i = 0; i < _frameSampleCount; i++)
            {
                short raw = BinaryPrimitives.ReadInt16LittleEndian(frameBytes.Slice(i * 2, 2));
                float normalized = raw / 32768f;
                samples[i] = normalized;

                double abs = Math.Abs(normalized);
                if (abs > peakAbs) peakAbs = abs;
                sumSquares += normalized * normalized;
            }
        }

        _ringBuffer.Write(samples);

        double rms = Math.Sqrt(sumSquares / _frameSampleCount);
        double rmsDb = ToDecibelsFullScale(rms);
        double peakDb = ToDecibelsFullScale(peakAbs);
        long frameIndex = Interlocked.Increment(ref _totalFramesProcessed);

        metrics = new AudioFrameMetrics(
            ChannelId: _channelId,
            SamplesProcessed: _frameSampleCount,
            RmsDb: Math.Round(rmsDb, 2),
            PeakDb: Math.Round(peakDb, 2),
            IsClipping: peakAbs >= ClippingThresholdLinear,
            IsDeadAir: rmsDb <= DeadAirThresholdDb,
            TotalFramesProcessed: frameIndex,
            UnderrunCount: _ringBuffer.UnderrunCount,
            OverrunCount: _ringBuffer.OverrunCount,
            BufferFillRatio: Math.Round(_ringBuffer.GetFillRatio(), 3),
            TimestampUtc: DateTimeOffset.UtcNow
        );
    }

    public static double ToDecibelsFullScale(double linearAmplitude)
    {
        if (linearAmplitude <= 1e-5)
            return MinDbFloor;

        double db = 20.0 * Math.Log10(linearAmplitude);
        return Math.Max(MinDbFloor, db);
    }

    /// <summary>
    /// Fills caller-owned byte span with synthetic 16-bit little-endian PCM sine wave audio.
    /// Useful for simulation, diagnostics, and deterministic unit tests without external WAV files.
    /// </summary>
    public static void WriteSineWavePcm16(Span<byte> destination, double frequencyHz = 440.0, int sampleRate = 16000, double amplitude = 0.5, double phaseOffsetSeconds = 0.0)
    {
        int sampleCount = destination.Length / BytesPerSample16Bit;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = phaseOffsetSeconds + (double)i / sampleRate;
            double value = Math.Sin(2.0 * Math.PI * frequencyHz * t) * amplitude;
            short sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), sample);
        }
    }
}

