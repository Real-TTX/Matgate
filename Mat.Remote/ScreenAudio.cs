using System.Runtime.InteropServices;

namespace Matgate.Display;

// Sound from the remote desktop for the viewer's page: whatever rate and channel count the server sends
// becomes 48 kHz stereo in pieces of 20 ms - what the page plays, and what the agent's screen sends too.
internal sealed class ScreenAudio(Action<ReadOnlyMemory<byte>> emit)
{
    private const int Rate = 48000;
    private const int FrameSamples = 960;

    // Sound arrives on the remote desktop's read loop; switching it on and off comes from the viewer.
    private readonly object _lock = new();
    private readonly short[] _pending = new short[FrameSamples * 2];
    private int _pendingFrames;
    // The resampling position, carried from one block to the next.
    private double _position;
    private short _lastLeft;
    private short _lastRight;
    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            lock (_lock)
            {
                _enabled = value;
                _pendingFrames = 0;
            }
        }
    }

    // Interleaved 16-bit samples, little endian.
    public void Add(ReadOnlySpan<byte> pcm, int rate, int channels)
    {
        if (!_enabled || rate <= 0 || channels <= 0)
        {
            return;
        }

        var samples = MemoryMarshal.Cast<byte, short>(pcm);
        var frames = samples.Length / channels;
        if (frames == 0)
        {
            return;
        }

        var second = channels > 1 ? 1 : 0;
        lock (_lock)
        {
            if (!_enabled)
            {
                return;
            }

            if (rate == Rate)
            {
                for (var i = 0; i < frames; i++)
                {
                    Push(samples[i * channels], samples[i * channels + second]);
                }

                return;
            }

            // Another rate (44.1 kHz, 22.05 kHz): linear interpolation between neighbouring samples.
            var step = (double)rate / Rate;
            while (_position < frames)
            {
                var index = (int)_position;
                var fraction = _position - index;
                var left0 = index == 0 ? _lastLeft : samples[(index - 1) * channels];
                var right0 = index == 0 ? _lastRight : samples[(index - 1) * channels + second];
                var left1 = samples[index * channels];
                var right1 = samples[index * channels + second];
                Push((short)(left0 + (left1 - left0) * fraction), (short)(right0 + (right1 - right0) * fraction));
                _position += step;
            }

            _position -= frames;
            _lastLeft = samples[(frames - 1) * channels];
            _lastRight = samples[(frames - 1) * channels + second];
        }
    }

    private void Push(short left, short right)
    {
        _pending[_pendingFrames * 2] = left;
        _pending[_pendingFrames * 2 + 1] = right;
        if (++_pendingFrames == FrameSamples)
        {
            emit(MemoryMarshal.AsBytes(_pending.AsSpan()).ToArray());
            _pendingFrames = 0;
        }
    }
}
