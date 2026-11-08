using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jarvis.App.Services;

public record Microphone(string Id, string Name);

public sealed class AudioService : IDisposable
{
    public static byte[] ReadyCue()
    {
        const int rate = 16000, samples = 1600;
        using var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(stream, new WaveFormat(rate, 16, 1)))
            for (var i = 0; i < samples; i++)
            {
                var envelope = Math.Pow(Math.Sin(Math.PI * i / samples), 2);
                writer.WriteSample((float)(0.22 * envelope * Math.Sin(2 * Math.PI * 880 * i / rate)));
            }
        return stream.ToArray();
    }

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private WasapiOut? _player;
    public event Action<byte[]>? Audio;
    public event Action<float>? Level;
    public event Action<float>? PlaybackLevel;
    public event Action<string>? Failed;

    public static string? DefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.ID;
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    public static List<Microphone> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<Microphone>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        { devices.Add(new(device.ID, device.FriendlyName)); device.Dispose(); }
        return devices;
    }

    public void Start(string id)
    {
        StopCapture();
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(id);
        var capture = new WasapiCapture(_device) { WaveFormat = new WaveFormat(16000, 16, 1) };
        _capture = capture;
        capture.DataAvailable += (_, args) =>
        {
            if (_capture != capture || args.BytesRecorded == 0) return;
            var data = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
            float peak = 0;
            for (int i = 0; i + 1 < data.Length; i += 2)
                peak = Math.Max(peak, Math.Abs((float)BitConverter.ToInt16(data, i)) / 32768f);
            Level?.Invoke(peak);
            Audio?.Invoke(data);
        };
        capture.RecordingStopped += (_, args) =>
        { if (args.Exception != null && _capture == capture) Failed?.Invoke(args.Exception.Message); };
        try { capture.StartRecording(); }
        catch { StopCapture(); throw; }
    }

    public void StopCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null) { capture.StopRecording(); capture.Dispose(); }
        _device?.Dispose(); _device = null;
        Level?.Invoke(0);
    }

    public async Task PlayAsync(byte[] wav, CancellationToken token, float volume = 0.8f)
    {
        using var stream = new MemoryStream(wav, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WasapiOut(AudioClientShareMode.Shared, 100);
        _player = output;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        { if (args.Exception != null) done.TrySetException(args.Exception); else done.TrySetResult(); };
        var gain = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = Math.Clamp(volume, 0, 1) };
        var meter = new MeteringSampleProvider(gain) { SamplesPerNotification = reader.WaveFormat.SampleRate / 25 };
        meter.StreamVolume += (_, args) => PlaybackLevel?.Invoke(args.MaxSampleValues.Max());
        output.Init(meter.ToWaveProvider());
        output.Play();
        using var registration = token.Register(() => output.Stop());
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(reader.TotalTime.TotalSeconds + 5, 30, 85)), token); }
        finally { output.Stop(); PlaybackLevel?.Invoke(0); if (_player == output) _player = null; }
    }

    public void StopPlayback() => _player?.Stop();
    public void Dispose() { StopCapture(); StopPlayback(); }
}
