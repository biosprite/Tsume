using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>Master volume of whatever Windows is currently playing through.</summary>
public static class AudioVolume
{
    private const int Render = 0;          // eRender
    private const int Multimedia = 1;      // eMultimedia
    private const int AllContexts = 0x17;  // CLSCTX_ALL

    public readonly record struct Reading(int Percent, bool Muted);

    public static Reading? Read() => WithEndpoint<Reading?>(endpoint =>
    {
        endpoint.GetMasterVolumeLevelScalar(out var level);
        endpoint.GetMute(out var muted);
        return new Reading((int)Math.Round(level * 100), muted);
    });

    public static void Set(int percent) => WithEndpoint<bool>(endpoint =>
    {
        endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, Guid.Empty);
        return true;
    });

    public static void SetMuted(bool muted) => WithEndpoint<bool>(endpoint =>
    {
        endpoint.SetMute(muted, Guid.Empty);
        return true;
    });

    /// <summary>Not cached: the default device changes when headphones or a dock are plugged in.</summary>
    private static T? WithEndpoint<T>(Func<IAudioEndpointVolume, T> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out device) != 0 || device is null)
                return default;

            if (device.Activate(typeof(IAudioEndpointVolume).GUID, AllContexts, 0, out var volume) != 0 || volume is null)
                return default;

            endpoint = (IAudioEndpointVolume)volume;
            return action(endpoint);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return default;
        }
        finally
        {
            if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }
}
