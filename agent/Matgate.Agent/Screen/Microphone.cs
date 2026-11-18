using Concentus;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Matgate.Agent.Screen;

// The microphone of the person in front of the browser, as a microphone of this machine.
//
// A program cannot be a microphone in Windows - that takes a driver, like a controller does. What a
// program can do is play sound into a device. So the voice is played into the input side of a virtual
// audio cable (VB-CABLE, VoiceMeeter, Steam's streaming microphone, ...): whatever is played there comes
// out of the cable's other end, and that end is a microphone every program can pick. Without such a
// cable there is no microphone here, and the page says why. "speakers" in the settings plays the voice on
// the machine's own loudspeakers instead - an intercom, not a microphone.
//
// On the wire: 20 ms packets, 48 kHz mono, Opus where the browser can encode it, 16-bit PCM otherwise.
public interface IMicrophone : IDisposable
{
    void Packet(string codec, ReadOnlyMemory<byte> data);
}

public static class MicrophoneDevices
{
    // The playback ends of the virtual cables people have, by what Windows calls them.
    private static readonly string[] Known =
    [
        "CABLE Input", "CABLE-A Input", "CABLE-B Input", "VoiceMeeter Input", "VoiceMeeter Aux Input", "Voicemeeter VAIO",
        "Steam Streaming Microphone", "Virtual Audio Cable", "Virtual Mic", "Virtual Microphone", "VAC ",
    ];

    // "ok" - there is a device to play the voice into; "nodevice" - none found; "off" - switched off in the settings.
    public static string State(string setting)
    {
        if (ScreenPlugin.TestMode)
        {
            return "ok";
        }

        if (IsOff(setting))
        {
            return "off";
        }

        try
        {
            using var device = Find(setting);
            return device is null ? "nodevice" : "ok";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException or InvalidCastException)
        {
            return "nodevice";
        }
    }

    public static bool IsOff(string setting) => setting.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);

    // The device the voice is played into: the one the settings name (a part of its name is enough,
    // "speakers" is the machine's default output), or the first virtual cable that is there.
    public static MMDevice? Find(string setting)
    {
        var wanted = setting.Trim();
        using var enumerator = new MMDeviceEnumerator();
        if (wanted.Equals("speakers", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
        }

        MMDevice? found = null;
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var name = device.FriendlyName;
            var fits = wanted.Length > 0
                ? name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                : Known.Any(known => name.Contains(known, StringComparison.OrdinalIgnoreCase));
            if (fits && found is null)
            {
                found = device;
            }
            else
            {
                device.Dispose();
            }
        }

        return found;
    }
}

// Decodes what arrives and hands on plain samples: 48 kHz, mono, 16 bit.
public abstract class MicrophoneBase : IMicrophone
{
    private readonly IOpusDecoder _opus = OpusCodecFactory.CreateDecoder(48000, 1);
    private readonly short[] _samples = new short[5760];

    public void Packet(string codec, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        try
        {
            if (codec == "opus")
            {
                var count = _opus.Decode(data.Span, _samples.AsSpan(), _samples.Length, false);
                if (count > 0)
                {
                    Samples(_samples.AsSpan(0, count));
                }
            }
            else
            {
                var count = Math.Min(data.Length / 2, _samples.Length);
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(data.Span[..(count * 2)]).CopyTo(_samples);
                Samples(_samples.AsSpan(0, count));
            }
        }
        catch (Exception exception) when (exception is OpusException or ArgumentException)
        {
            // A packet that cannot be decoded is 20 ms of silence, not the end of the microphone.
        }
    }

    protected abstract void Samples(ReadOnlySpan<short> samples);

    public virtual void Dispose()
    {
        (_opus as IDisposable)?.Dispose();
    }
}

// For the tests: what arrived, said instead of played - how much, and how loud (so silence is told from a voice).
public sealed class RecordingMicrophone(Action<string> report) : MicrophoneBase
{
    private int _packets;
    private double _energy;
    private long _count;

    protected override void Samples(ReadOnlySpan<short> samples)
    {
        foreach (var sample in samples)
        {
            _energy += (double)sample * sample;
        }

        _count += samples.Length;
        if (++_packets % 25 == 0)
        {
            var level = _count == 0 ? 0 : (int)Math.Round(Math.Sqrt(_energy / _count));
            report($"mic packets={_packets} samples={_count} level={level}");
        }
    }

    public override void Dispose()
    {
        report($"mic closed after {_packets} packets");
        base.Dispose();
    }
}

public sealed class MicrophonePlayer : MicrophoneBase
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly BufferedWaveProvider _buffer;
    private readonly byte[] _bytes = new byte[5760 * 4];

    public MicrophonePlayer(string setting)
    {
        _device = MicrophoneDevices.Find(setting)
            ?? throw new InvalidOperationException("This machine has no virtual microphone to play the voice into.");
        Name = _device.FriendlyName;
        // Stereo, because that is what every playback device takes; Windows converts the rest.
        _buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2), TimeSpan.FromMilliseconds(600))
        {
            DiscardOnBufferOverflow = true,
        };
        _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
        _output.Init(_buffer);
        _output.Play();
    }

    public string Name { get; }

    protected override void Samples(ReadOnlySpan<short> samples)
    {
        // A voice is worth more on time than complete: what piled up (a stall on the way) is dropped
        // rather than played late.
        if (_buffer.BufferedDuration > TimeSpan.FromMilliseconds(240))
        {
            _buffer.ClearBuffer();
        }

        var bytes = _bytes.AsSpan(0, samples.Length * 4);
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes[(i * 4)..], samples[i]);
            BitConverter.TryWriteBytes(bytes[(i * 4 + 2)..], samples[i]);
        }

        _buffer.AddSamples(_bytes, 0, bytes.Length);
    }

    public override void Dispose()
    {
        try
        {
            _output.Stop();
            _output.Dispose();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A device that went away takes its stream with it.
        }

        _device.Dispose();
        base.Dispose();
    }
}
