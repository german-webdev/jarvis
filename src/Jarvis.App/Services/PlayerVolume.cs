using System.Diagnostics;
using System.IO;
using Jarvis.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Jarvis.App.Services;

public static class PlayerVolume
{
    public static SkillResult Change(string executable, string name, float delta, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToArray();
        var opened = new List<AudioSessionControl>();
        var matches = new List<AudioSessionControl>();
        try
        {
            foreach (var device in devices)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i]; opened.Add(session);
                    try
                    {
                        if (session.State == AudioSessionState.AudioSessionStateExpired || session.GetProcessID == 0) continue;
                        using var process = Process.GetProcessById((int)session.GetProcessID);
                        if (string.Equals(process.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) matches.Add(session);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { }
                }
            }
            // Never change the system mixer or an unrelated application as a fallback.
            if (matches.Count == 0) return new(false, "Аудиоканал выбранного плеера не найден. Начни воспроизведение и повтори команду.");
            if (matches.Count > 1) return new(false, "У плеера несколько аудиоканалов. Оставь одно воспроизведение и повтори команду.");
            token.ThrowIfCancellationRequested();
            var volume = matches[0].SimpleAudioVolume;
            float expected = Math.Clamp(volume.Volume + delta, 0, 1);
            volume.Volume = expected;
            float actual = volume.Volume;
            bool verified = Math.Abs(actual - expected) < 0.005f;
            return new(verified, verified ? $"{name}: громкость {Math.Round(actual * 100)} процентов." + (volume.Mute ? " Звук плеера выключен." : "") : "Плеер не подтвердил изменение громкости.");
        }
        finally
        {
            foreach (var session in opened) session.Dispose();
            foreach (var device in devices) device.Dispose();
        }
    }
}
