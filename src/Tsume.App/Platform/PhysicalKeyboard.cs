using System.Runtime.InteropServices;
using Tsume.App.Platform.Interop;

namespace Tsume.App.Platform;

/// <summary>
/// Detects whether an external USB or Bluetooth physical keyboard is connected, ignoring the virtual,
/// ACPI, GPIO-button, and built-in handheld controller keyboard interfaces Windows enumerates on handhelds.
/// </summary>
public static class PhysicalKeyboard
{
    /// <summary>Minimum keys reported by a real typing keyboard (excludes power/volume/macro button stubs).</summary>
    private const uint MinTypingKeys = 40;

    /// <summary>Bluetooth Classic HID service GUID present in Raw Input device paths.</summary>
    private const string BluetoothClassicHidGuid = "{00001124-0000-1000-8000-00805f9b34fb}";

    /// <summary>Bluetooth Low Energy (HOGP) HID service GUID present in Raw Input device paths.</summary>
    private const string BluetoothLeHidGuid = "{00001812-0000-1000-8000-00805f9b34fb}";

    public static bool IsConnected()
    {
        var entrySize = (uint)Marshal.SizeOf<User32.RAWINPUTDEVICELIST>();
        uint count = 0;
        if (User32.GetRawInputDeviceList(null, ref count, entrySize) != 0 || count == 0)
            return false;

        var devices = new User32.RAWINPUTDEVICELIST[count];
        var read = User32.GetRawInputDeviceList(devices, ref count, entrySize);
        if (read == unchecked((uint)-1))
            return false;

        for (var i = 0; i < read; i++)
        {
            if (devices[i].Type != User32.RIM_TYPEKEYBOARD) continue;

            var name = DeviceNameOf(devices[i].Device);
            if (string.IsNullOrEmpty(name)) continue;
            if (!IsExternalHidKeyboardPath(name)) continue;

            var info = new User32.RID_DEVICE_INFO { Size = (uint)Marshal.SizeOf<User32.RID_DEVICE_INFO>() };
            var infoSize = info.Size;
            if (User32.GetRawInputDeviceInfo(devices[i].Device, User32.RIDI_DEVICEINFO, ref info, ref infoSize) == unchecked((uint)-1))
                continue;

            if (info.Keyboard.NumberOfKeysTotal >= MinTypingKeys)
                return true;
        }

        return false;
    }

    private static string? DeviceNameOf(nint device)
    {
        uint chars = 0;
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, null, ref chars) != 0 || chars == 0)
            return null;

        var buffer = new char[chars];
        if (User32.GetRawInputDeviceInfo(device, User32.RIDI_DEVICENAME, buffer, ref chars) == unchecked((uint)-1))
            return null;

        return new string(buffer).TrimEnd('\0');
    }

    /// <summary>
    /// True for a USB or Bluetooth HID keyboard path, excluding the built-in handheld controller,
    /// internal ACPI/PS2 stubs, GPIO button converters, Intel sensor hubs, and virtual RDP keyboards.
    /// </summary>
    internal static bool IsExternalHidKeyboardPath(string path)
    {
        if (!path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase))
            return false;

        if (ClawController.IsClaw(path))
            return false;

        var isUsb = path.Contains("VID_", StringComparison.OrdinalIgnoreCase)
            && path.Contains("PID_", StringComparison.OrdinalIgnoreCase);

        var isBluetooth = path.Contains(BluetoothClassicHidGuid, StringComparison.OrdinalIgnoreCase)
            || path.Contains(BluetoothLeHidGuid, StringComparison.OrdinalIgnoreCase);

        return isUsb || isBluetooth;
    }
}
