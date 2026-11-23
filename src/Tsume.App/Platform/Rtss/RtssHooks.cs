using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Platform.Rtss;

/// <summary>
/// Dynamically binds to RTSS's own <c>RTSSHooks64.dll</c> in its installation folder (zero copied files)
/// to read/toggle runtime flags and broadcast live profile updates to hooked 3D games, plus reads
/// RTSS's <c>Profiles\Global</c> (falling back to <c>ProfileTemplates\Global</c>).
/// </summary>
public sealed class RtssHooks : IDisposable
{
    private const uint FlagOsdVisible = 0x00000001;
    private const uint FlagLimiterDisabled = 0x00000004;

    private const string GlobalProfile = "";
    private const string PropFramerateLimit = "FramerateLimit";
    private const string PropPositionX = "PositionX";
    private const string PropPositionY = "PositionY";
    private const string PropZoomRatio = "ZoomRatio";
    private const string PropEnableFill = "EnableFill";
    private const string PropFillColor = "FillColor";

    private static readonly Journal Log = new("rtss");

    private nint _library;
    private string? _loadedPath;

    private GetFlagsFn? _getFlags;
    private SetFlagsFn? _setFlags;
    private LoadProfileFn? _loadProfile;
    private SaveProfileFn? _saveProfile;
    private GetProfilePropertyFn? _getProfileProperty;
    private SetProfilePropertyFn? _setProfileProperty;
    private UpdateProfilesFn? _updateProfiles;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetFlagsFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint SetFlagsFn(uint dwAnd, uint dwXor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate void LoadProfileFn(string profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate void SaveProfileFn(string profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool GetProfilePropertyFn(string name, nint data, uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetProfilePropertyFn(string name, nint data, uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UpdateProfilesFn();

    /// <summary>Loads <c>RTSSHooks64.dll</c> directly from RTSS's install folder if not already loaded.</summary>
    public bool EnsureLoaded(string? hooksDllPath)
    {
        if (string.IsNullOrWhiteSpace(hooksDllPath)) return false;
        if (_library != 0 && string.Equals(_loadedPath, hooksDllPath, StringComparison.OrdinalIgnoreCase))
            return true;

        Unload();

        if (!NativeLibrary.TryLoad(hooksDllPath, out var handle))
        {
            Log.Write($"could not load {hooksDllPath}");
            return false;
        }

        _library = handle;
        _loadedPath = hooksDllPath;

        _getFlags = Resolve<GetFlagsFn>("GetFlags");
        _setFlags = Resolve<SetFlagsFn>("SetFlags");
        _loadProfile = Resolve<LoadProfileFn>("LoadProfile");
        _saveProfile = Resolve<SaveProfileFn>("SaveProfile");
        _getProfileProperty = Resolve<GetProfilePropertyFn>("GetProfileProperty");
        _setProfileProperty = Resolve<SetProfilePropertyFn>("SetProfileProperty");
        _updateProfiles = Resolve<UpdateProfilesFn>("UpdateProfiles");

        return _getFlags is not null || _loadProfile is not null;
    }

    public bool? IsOsdVisible()
    {
        if (_getFlags is null) return null;
        try { return (_getFlags() & FlagOsdVisible) != 0; }
        catch { return null; }
    }

    public bool SetOsdVisible(bool visible)
    {
        if (_setFlags is null) return false;
        try
        {
            _setFlags(~FlagOsdVisible, visible ? FlagOsdVisible : 0u);
            return true;
        }
        catch (Exception error)
        {
            Log.Write($"SetOsdVisible({visible}) failed: {error.Message}");
            return false;
        }
    }

    public void EnsureLimiterEnabled()
    {
        if (_setFlags is null) return;
        try { _setFlags(~FlagLimiterDisabled, 0u); }
        catch (Exception error) { Log.Write($"EnsureLimiterEnabled failed: {error.Message}"); }
    }

    /// <summary>
    /// Tells RTSS and every active hooked 3D application to reload <c>Profiles\Global</c> right away.
    /// </summary>
    public void NotifyProfilesUpdated()
    {
        try { _updateProfiles?.Invoke(); }
        catch (Exception error) { Log.Write($"UpdateProfiles failed: {error.Message}"); }

        RtssSharedMemory.RequestProfileUpdate();
    }

    /// <summary>Reads the effective global frame-rate cap from RTSS's profile file (with DLL fallback).</summary>
    public int? GetFrameRateLimit(string? rtssFolder)
    {
        if (_getFlags is not null)
        {
            try
            {
                if ((_getFlags() & FlagLimiterDisabled) != 0) return 0;
            }
            catch { }
        }

        if (rtssFolder is not null && ReadProfileValues(rtssFolder) is { } values
            && values.TryGetValue("Framerate.Limit", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileLimit))
        {
            return Math.Max(0, fileLimit);
        }

        if (_loadProfile is null || _getProfileProperty is null) return null;

        try
        {
            _loadProfile(GlobalProfile);
            return TryReadInt(PropFramerateLimit, out var limit) ? Math.Max(0, limit) : null;
        }
        catch (Exception error)
        {
            Log.Write($"GetFrameRateLimit failed: {error.Message}");
            return null;
        }
    }

    /// <summary>Direct fallback for writing Global profile properties when the helper service is offline.</summary>
    public bool TryWriteProfileDirect(int? limit = null, int? x = null, int? y = null, int? zoom = null, int? enableFill = null, uint? fillColor = null)
    {
        if (_loadProfile is null || _setProfileProperty is null || _saveProfile is null) return false;

        try
        {
            _loadProfile(GlobalProfile);
            if (limit is { } l) TryWriteInt(PropFramerateLimit, Math.Clamp(l, 0, RtssSettings.MaxCustomFps));
            if (x is { } px) TryWriteInt(PropPositionX, px);
            if (y is { } py) TryWriteInt(PropPositionY, py);
            if (zoom is { } pz) TryWriteInt(PropZoomRatio, Math.Clamp(pz, 1, 8));
            if (enableFill is { } pf) TryWriteInt(PropEnableFill, pf != 0 ? 1 : 0);
            if (fillColor is { } pc) TryWriteUInt(PropFillColor, pc);
            _saveProfile(GlobalProfile);
            return true;
        }
        catch (Exception error)
        {
            Log.Write($"TryWriteProfileDirect failed: {error.Message}");
            return false;
        }
    }

    private static Dictionary<string, string>? ReadProfileValues(string rtssFolder)
    {
        var globalFile = Path.Combine(rtssFolder, "Profiles", "Global");
        var templateFile = Path.Combine(rtssFolder, "ProfileTemplates", "Global");
        var path = File.Exists(globalFile) ? globalFile : File.Exists(templateFile) ? templateFile : null;
        if (path is null) return null;

        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var section = "";

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                map[section.Length > 0 ? $"{section}.{key}" : key] = value;
            }

            return map;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? ParseInt(Dictionary<string, string>? map, string key) =>
        map is not null && map.TryGetValue(key, out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static uint? ParseHexUInt(Dictionary<string, string>? map, string key) =>
        map is not null && map.TryGetValue(key, out var raw)
        && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private unsafe bool TryReadInt(string name, out int value)
    {
        value = 0;
        if (_getProfileProperty is null) return false;
        fixed (int* p = &value)
            return _getProfileProperty(name, (nint)p, sizeof(int));
    }

    private unsafe bool TryWriteInt(string name, int value)
    {
        if (_setProfileProperty is null) return false;
        return _setProfileProperty(name, (nint)(&value), sizeof(int));
    }

    private unsafe bool TryReadUInt(string name, out uint value)
    {
        value = 0;
        if (_getProfileProperty is null) return false;
        fixed (uint* p = &value)
            return _getProfileProperty(name, (nint)p, sizeof(uint));
    }

    private unsafe bool TryWriteUInt(string name, uint value)
    {
        if (_setProfileProperty is null) return false;
        return _setProfileProperty(name, (nint)(&value), sizeof(uint));
    }

    private T? Resolve<T>(string name) where T : Delegate =>
        _library != 0 && NativeLibrary.TryGetExport(_library, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private void Unload()
    {
        _getFlags = null;
        _setFlags = null;
        _loadProfile = null;
        _saveProfile = null;
        _getProfileProperty = null;
        _setProfileProperty = null;
        _updateProfiles = null;
        _loadedPath = null;

        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    public void Dispose() => Unload();
}
