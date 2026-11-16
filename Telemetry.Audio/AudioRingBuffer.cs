namespace Telemetry.Audio;

/// <summary>
/// Fixed-capacity, pre-allocated circular (ring) buffer for normalized 32-bit PCM audio samples.
/// Designed for zero heap allocations on real-time producer/consumer hot paths:
/// all read/write operations accept <see cref="Span{T}"/> / <see cref="ReadOnlySpan{T}"/>
/// and perform contiguous block copies across circular wrap boundaries.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly object _sync = new();
    private int _head; // Next write index
    private int _tail; // Next read index
    private int _count;

    public int Capacity => _buffer.Length;

    public int AvailableToRead
    {
        get { lock (_sync) return _count; }
    }

    public int AvailableToWrite
    {
        get { lock (_sync) return _buffer.Length - _count; }
    }

    public long UnderrunCount { get; private set; }
    public long OverrunCount { get; private set; }

    public AudioRingBuffer(int capacitySamples)
    {
        if (capacitySamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacitySamples), "Capacity must be positive.");

        _buffer = new float[capacitySamples];
    }

    /// <summary>
    /// Writes incoming PCM samples into the ring buffer without allocating memory.
    /// If incoming samples exceed free capacity, the oldest buffered samples are overwritten
    /// to preserve real-time low latency and <see cref="OverrunCount"/> is incremented.
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return 0;

        lock (_sync)
        {
            // If a single burst is larger than total capacity, keep only the newest tail window.
            if (samples.Length >= _buffer.Length)
            {
                OverrunCount += (samples.Length - _buffer.Length) + _count;
                samples = samples[^_buffer.Length..];
                samples.CopyTo(_buffer);
                _head = 0;
                _tail = 0;
                _count = _buffer.Length;
                return _buffer.Length;
            }

            int free = _buffer.Length - _count;
            if (samples.Length > free)
            {
                int overflow = samples.Length - free;
                OverrunCount += overflow;
                // Advance read tail to drop oldest samples and make room for fresh audio.
                _tail = (_tail + overflow) % _buffer.Length;
                _count -= overflow;
            }

            int firstChunk = Math.Min(samples.Length, _buffer.Length - _head);
            samples[..firstChunk].CopyTo(_buffer.AsSpan(_head, firstChunk));

            int remaining = samples.Length - firstChunk;
            if (remaining > 0)
            {
                samples[firstChunk..].CopyTo(_buffer.AsSpan(0, remaining));
            }

            _head = (_head + samples.Length) % _buffer.Length;
            _count += samples.Length;
            return samples.Length;
        }
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length samples into caller-owned stack/pooled memory.
    /// If insufficient samples are buffered (jitter/network stall), fills the shortfall with silence (<c>0f</c>)
    /// and increments <see cref="UnderrunCount"/> so downstream audio playback never glitches on stale memory.
    /// </summary>
    public int Read(Span<float> destination)
    {
        if (destination.IsEmpty)
            return 0;

        lock (_sync)
        {
            int toRead = Math.Min(destination.Length, _count);
            if (toRead > 0)
            {
                int firstChunk = Math.Min(toRead, _buffer.Length - _tail);
                _buffer.AsSpan(_tail, firstChunk).CopyTo(destination[..firstChunk]);

                int remaining = toRead - firstChunk;
                if (remaining > 0)
                {
                    _buffer.AsSpan(0, remaining).CopyTo(destination.Slice(firstChunk, remaining));
                }

                _tail = (_tail + toRead) % _buffer.Length;
                _count -= toRead;
            }

            if (toRead < destination.Length)
            {
                UnderrunCount++;
                destination[toRead..].Clear();
            }

            return toRead;
        }
    }

    public double GetFillRatio()
    {
        lock (_sync)
        {
            return (double)_count / _buffer.Length;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            Array.Clear(_buffer);
            _head = 0;
            _tail = 0;
            _count = 0;
            UnderrunCount = 0;
            OverrunCount = 0;
        }
    }
}

