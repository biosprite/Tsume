using System.Runtime.InteropServices;

namespace Tsume.App.Platform.Interop;

// Core Audio interfaces for the master volume. The *_Unused methods are placeholders
// that keep each COM vtable in order — do not remove or reorder them.

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints_Unused();

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid interfaceId, int context, nint activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify_Unused();
    int UnregisterControlChangeNotify_Unused();
    int GetChannelCount_Unused();
    int SetMasterVolumeLevel_Unused();

    [PreserveSig] int SetMasterVolumeLevelScalar(float level, in Guid eventContext);

    int GetMasterVolumeLevel_Unused();

    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);

    int SetChannelVolumeLevel_Unused();
    int SetChannelVolumeLevelScalar_Unused();
    int GetChannelVolumeLevel_Unused();
    int GetChannelVolumeLevelScalar_Unused();

    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, in Guid eventContext);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}
