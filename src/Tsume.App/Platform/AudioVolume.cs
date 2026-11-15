using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>Master volume of whatever Windows is currently playing through.</summary>
public static class AudioVolume
{
    private const int Render = 0;          // eRender
    private const int Multimedia = 1;      // eMultimedia
    private const int AllContexts = 0x17;  // CLSCTX_ALL

    public readonly record struct Reading(int Percent, bool Muted);

    public static Reading? Read()
    {
        if (Endpoint() is not { } endpoint) return null;

        endpoint.GetMasterVolumeLevelScalar(out var level);
        endpoint.GetMute(out var muted);
        return new Reading((int)Math.Round(level * 100), muted);
    }

    public static void Set(int percent) =>
        Endpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty);

    public static void SetMuted(bool muted) =>
        Endpoint()?.SetMute(muted, Guid.Empty);

    /// <summary>Not cached: the default device changes when headphones or a dock are plugged in.</summary>
    private static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0) return null;

            return device.Activate(typeof(IAudioEndpointVolume).GUID, AllContexts, 0, out var volume) == 0
                ? (IAudioEndpointVolume)volume
                : null;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return null;
        }
    }
}
