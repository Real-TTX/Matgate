using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace Matgate.Agent.Screen;

// What the machine plays (WASAPI loopback of the default output), in 20 ms packets: Opus where the browser
// can decode it, otherwise plain 16-bit PCM. Always 48 kHz stereo on the wire.
public sealed class AudioStreamer : IDisposable
{
    public const int SampleRate = 48000;
    private const int FrameSamples = 960;

    private readonly WasapiLoopbackCapture _capture;
    private readonly IOpusEncoder? _opus;
    private readonly Action<byte[]> _send;
    private readonly float[] _frame = new float[FrameSamples * 2];
    private readonly byte[] _packet = new byte[4000];
    private readonly int _channels;
    private readonly int _rate;
    private readonly bool _float;
    private readonly int _bytesPerSample;
    private int _filled;
    private double _position;
    private float _lastLeft;
    private float _lastRight;

    public AudioStreamer(bool opus, Action<byte[]> send)
    {
        _send = send;
        _capture = new WasapiLoopbackCapture();
        var format = _capture.WaveFormat;
        _channels = format.Channels;
        _rate = format.SampleRate;
        _bytesPerSample = format.BitsPerSample / 8;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        if (opus)
        {
            _opus = OpusCodecFactory.CreateEncoder(SampleRate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
            _opus.Bitrate = 128000;
        }

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    public string Codec => _opus is null ? "pcm" : "opus";

    private void OnData(object? sender, WaveInEventArgs data)
    {
        var frameBytes = _bytesPerSample * _channels;
        if (frameBytes == 0)
        {
            return;
        }

        var frames = data.BytesRecorded / frameBytes;
        var step = (double)_rate / SampleRate;
        for (var i = 0; i < frames; i++)
        {
            var left = Sample(data.Buffer, i * frameBytes);
            var right = _channels > 1 ? Sample(data.Buffer, i * frameBytes + _bytesPerSample) : left;
            if (_rate == SampleRate)
            {
                Push(left, right);
                continue;
            }

            // Linear resampling to 48 kHz: good enough for a remote screen's sound.
            while (_position <= 1)
            {
                Push(_lastLeft + (float)((left - _lastLeft) * _position), _lastRight + (float)((right - _lastRight) * _position));
                _position += step;
            }

            _position -= 1;
            _lastLeft = left;
            _lastRight = right;
        }
    }

    private float Sample(byte[] buffer, int offset)
    {
        if (_float && _bytesPerSample == 4)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => ((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24)) / 2147483648f,
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0,
        };
    }

    private void Push(float left, float right)
    {
        _frame[_filled * 2] = left;
        _frame[_filled * 2 + 1] = right;
        if (++_filled < FrameSamples)
        {
            return;
        }

        _filled = 0;
        if (_opus is not null)
        {
            var length = _opus.Encode(_frame, FrameSamples, _packet, _packet.Length);
            if (length > 0)
            {
                _send(_packet[..length]);
            }

            return;
        }

        var pcm = new byte[FrameSamples * 4];
        for (var i = 0; i < FrameSamples * 2; i++)
        {
            var value = (short)Math.Clamp(_frame[i] * 32767f, -32768f, 32767f);
            pcm[i * 2] = (byte)value;
            pcm[i * 2 + 1] = (byte)(value >> 8);
        }

        _send(pcm);
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        _capture.Dispose();
    }
}

// For the tests: a quiet 440 Hz tone in the same packets, instead of whatever the machine plays.
public sealed class TestTone : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly IOpusEncoder? _opus;
    private readonly float[] _frame = new float[960 * 2];
    private readonly byte[] _packet = new byte[4000];
    private long _sample;

    public TestTone(bool opus, Action<byte[]> send)
    {
        if (opus)
        {
            _opus = OpusCodecFactory.CreateEncoder(AudioStreamer.SampleRate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        }

        _timer = new System.Threading.Timer(_ =>
        {
            lock (_frame)
            {
                for (var i = 0; i < 960; i++, _sample++)
                {
                    _frame[i * 2] = _frame[i * 2 + 1] = 0.1f * MathF.Sin(2 * MathF.PI * 440 * _sample / AudioStreamer.SampleRate);
                }

                if (_opus is not null)
                {
                    var length = _opus.Encode(_frame, 960, _packet, _packet.Length);
                    send(_packet[..length]);
                    return;
                }

                var pcm = new byte[960 * 4];
                for (var i = 0; i < 960 * 2; i++)
                {
                    var value = (short)(_frame[i] * 32767f);
                    pcm[i * 2] = (byte)value;
                    pcm[i * 2 + 1] = (byte)(value >> 8);
                }

                send(pcm);
            }
        }, null, 0, 20);
    }

    public void Dispose() => _timer.Dispose();
}
