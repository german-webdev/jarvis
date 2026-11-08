using Jarvis.Core;
using NAudio.CoreAudioApi;

namespace Jarvis.App.Services;

public static class VolumeControl
{
    public static SkillResult Apply(CommandRequest request)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var volume = device.AudioEndpointVolume;
        switch (request.Intent)
        {
            case Intent.Mute: volume.Mute = true; break;
            case Intent.Unmute: volume.Mute = false; break;
            case Intent.VolumeUp: volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + 0.1f, 0, 1); break;
            case Intent.VolumeDown: volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar - 0.1f, 0, 1); break;
            case Intent.SetVolume: volume.MasterVolumeLevelScalar = Math.Clamp(request.Value!.Value / 100f, 0, 1); break;
            default: throw new InvalidOperationException();
        }
        var muted = volume.Mute;
        var percent = (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
        string message = request.Intent == Intent.Mute ? "Звук выключен." : request.Intent == Intent.Unmute
            ? $"Звук включён. Громкость {percent} процентов." : $"Громкость {percent} процентов." + (muted ? " Звук по-прежнему выключен." : "");
        return new(true, message, Silent: muted || percent == 0);
    }
}
