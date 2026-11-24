using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Tsume.App.Controls;
using Tsume.App.Platform.Interop;
using Tsume.Core.Controller;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

public enum DedicatedActionCategory
{
    Tsume,
    System,
}

public enum DedicatedAction
{
    OpenFpsOverlay,
    OpenTsume,
    CyclePerformanceMode,
    CycleRefreshRate,
    ToggleFanBoost,
    ToggleControllerMode,
    Screenshot,
    TouchKeyboard,
    ShowDesktop,
    TaskManager,
    ToggleMute,
    SteamBigPicture,
    XboxGameBar,
}

public sealed record DedicatedActionBinding(DedicatedAction Action, IReadOnlyList<byte> Keys);

/// <summary>
/// Maps <see cref="BackButtonKey.Code"/> values to Win32 virtual keys or XInput gamepad buttons so
/// Dedicated Actions can share the exact same key picker style as Back Buttons.
/// </summary>
public static class DedicatedActionKeys
{
    public const int MaxKeys = BackButtonBinding.MaxKeys;
    public const byte XboxButtonCode = 13;
    public const byte BackLeftCode = 17;
    public const byte BackRightCode = 18;
    public const byte OemButtonCode = 250;

    public static readonly BackButtonKey OemButtonKey = new(OemButtonCode, "Quick Menu / OEM", "Hardware");
    private static readonly BackButtonKey XboxButtonKey = new(XboxButtonCode, "Xbox / Guide", "Gamepad");
    private static readonly BackButtonKey BackLeftKey = new(BackLeftCode, "Back Left", "Gamepad");
    private static readonly BackButtonKey BackRightKey = new(BackRightCode, "Back Right", "Gamepad");

    internal readonly record struct KeySpec(uint Vk, bool? Extended = null, uint? AltVk = null, bool IsModifier = false);

    [Flags]
    internal enum GamepadInput : uint
    {
        None      = 0,
        DPadUp    = 0x0001,
        DPadDown  = 0x0002,
        DPadLeft  = 0x0004,
        DPadRight = 0x0008,
        Menu      = 0x0010,
        View      = 0x0020,
        LsClick   = 0x0040,
        RsClick   = 0x0080,
        Lb        = 0x0100,
        Rb        = 0x0200,
        Guide     = 0x0400,
        A         = 0x1000,
        B         = 0x2000,
        X         = 0x4000,
        Y         = 0x8000,
        Lt        = 0x10000,
        Rt        = 0x20000,
        BackLeft  = 0x40000,
        BackRight = 0x80000,
    }

    private const byte TriggerThreshold = 128;

    private static readonly Dictionary<byte, KeySpec> KeyboardMap = new()
    {
        // Modifiers
        [116] = new(0xA2, IsModifier: true),                  // Left Ctrl
        [104] = new(0xA0, IsModifier: true),                  // Left Shift
        [118] = new(0xA4, IsModifier: true),                  // Left Alt
        [117] = new(0x5B, AltVk: 0x5C, IsModifier: true),     // Win (LWin or RWin)
        [121] = new(0xA3, IsModifier: true),                  // Right Ctrl
        [115] = new(0xA1, IsModifier: true),                  // Right Shift
        [120] = new(0xA5, IsModifier: true),                  // Right Alt

        // Common / Navigation & Editing
        [50]  = new(0x1B),                                    // Esc
        [103] = new(0x0D, Extended: false),                   // Enter
        [119] = new(0x20),                                    // Space
        [77]  = new(0x09),                                    // Tab
        [76]  = new(0x08),                                    // Backspace
        [122] = new(0x2D),                                    // Insert
        [125] = new(0x2E),                                    // Delete
        [123] = new(0x24),                                    // Home
        [126] = new(0x23),                                    // End
        [124] = new(0x21),                                    // Page Up
        [127] = new(0x22),                                    // Page Down
        [91]  = new(0x14),                                    // Caps Lock

        // Arrow keys
        [128] = new(0x26),                                    // Up Arrow
        [129] = new(0x28),                                    // Down Arrow
        [130] = new(0x25),                                    // Left Arrow
        [131] = new(0x27),                                    // Right Arrow

        // Letters A-Z
        [92]  = new(0x41), [109] = new(0x42), [107] = new(0x43), [94]  = new(0x44), [80]  = new(0x45),
        [95]  = new(0x46), [96]  = new(0x47), [97]  = new(0x48), [85]  = new(0x49), [98]  = new(0x4A),
        [99]  = new(0x4B), [100] = new(0x4C), [111] = new(0x4D), [110] = new(0x4E), [86]  = new(0x4F),
        [87]  = new(0x50), [78]  = new(0x51), [81]  = new(0x52), [93]  = new(0x53), [82]  = new(0x54),
        [84]  = new(0x55), [108] = new(0x56), [79]  = new(0x57), [106] = new(0x58), [83]  = new(0x59),
        [105] = new(0x5A),

        // Number row 1-0
        [64]  = new(0x31), [65]  = new(0x32), [66]  = new(0x33), [67]  = new(0x34), [68]  = new(0x35),
        [69]  = new(0x36), [70]  = new(0x37), [71]  = new(0x38), [72]  = new(0x39), [73]  = new(0x30),

        // Function keys
        [51]  = new(0x70), [52]  = new(0x71), [53]  = new(0x72), [54]  = new(0x73), [55]  = new(0x74),
        [56]  = new(0x75), [57]  = new(0x76), [58]  = new(0x77), [59]  = new(0x78), [60]  = new(0x79),
        [61]  = new(0x7A), [62]  = new(0x7B),
        [157] = new(0x85),                                    // F22
        [156] = new(0x86),                                    // F23

        // Symbols
        [63]  = new(0xC0),                                    // `
        [74]  = new(0xBD),                                    // -
        [75]  = new(0xBB),                                    // =
        [88]  = new(0xDB),                                    // [
        [89]  = new(0xDD),                                    // ]
        [90]  = new(0xDC),                                    // \
        [101] = new(0xBA),                                    // ;
        [102] = new(0xDE),                                    // '
        [112] = new(0xBC),                                    // ,
        [113] = new(0xBE),                                    // .
        [114] = new(0xBF),                                    // /

        // Numpad
        [132] = new(0x90),                                    // Num Lock
        [139] = new(0x60), [140] = new(0x61), [141] = new(0x62), [142] = new(0x63), [143] = new(0x64),
        [144] = new(0x65), [145] = new(0x66), [146] = new(0x67), [147] = new(0x68), [148] = new(0x69),
        [133] = new(0x6F),                                    // Numpad /
        [134] = new(0x6A),                                    // Numpad *
        [135] = new(0x6D),                                    // Numpad -
        [136] = new(0x6B),                                    // Numpad +
        [137] = new(0x6E),                                    // Numpad .
        [138] = new(0x0D, Extended: true),                    // Numpad Enter

        // Media
        [149] = new(0xB3),                                    // Play / Pause
        [150] = new(0xB2),                                    // Stop
        [151] = new(0xB0),                                    // Next Track
        [152] = new(0xB1),                                    // Previous Track
        [153] = new(0xAF),                                    // Volume Up
        [154] = new(0xAE),                                    // Volume Down
        [155] = new(0xAD),                                    // Mute
    };

    private static readonly Dictionary<byte, GamepadInput> GamepadMap = new()
    {
        [9]  = GamepadInput.A,
        [10] = GamepadInput.B,
        [11] = GamepadInput.X,
        [12] = GamepadInput.Y,
        [5]  = GamepadInput.Lb,
        [6]  = GamepadInput.Rb,
        [19] = GamepadInput.Lt,
        [20] = GamepadInput.Rt,
        [7]  = GamepadInput.LsClick,
        [8]  = GamepadInput.RsClick,
        [14] = GamepadInput.View,
        [15] = GamepadInput.Menu,
        [XboxButtonCode] = GamepadInput.Guide,
        [BackLeftCode]   = GamepadInput.BackLeft,
        [BackRightCode]  = GamepadInput.BackRight,
        [1]  = GamepadInput.DPadUp,
        [2]  = GamepadInput.DPadDown,
        [3]  = GamepadInput.DPadLeft,
        [4]  = GamepadInput.DPadRight,
    };

    /// <summary>Supported keys for Dedicated Actions, using device-agnostic names for hardware and back buttons.</summary>
    public static IReadOnlyList<BackButtonKey> All { get; } = BuildAll();

    private static readonly Dictionary<byte, BackButtonKey> Lookup = All.ToDictionary(k => k.Code);

    public static BackButtonKey FromCode(byte code) =>
        Lookup.TryGetValue(code, out var found) ? found : BackButtonKey.FromCode(code);

    private static ReadOnlyCollection<BackButtonKey> BuildAll()
    {
        var list = new List<BackButtonKey>
        {
            BackButtonKey.Unbound,
            OemButtonKey,
            XboxButtonKey,
        };
        var seen = new HashSet<byte>
        {
            BackButtonKey.UnboundCode,
            OemButtonCode,
            XboxButtonCode,
        };

        foreach (var key in BackButtonKey.All)
        {
            if (!seen.Add(key.Code)) continue;

            if (key.Code == BackLeftCode) list.Add(BackLeftKey);
            else if (key.Code == BackRightCode) list.Add(BackRightKey);
            else if (KeyboardMap.ContainsKey(key.Code) || GamepadMap.ContainsKey(key.Code)) list.Add(key);
        }

        return new ReadOnlyCollection<BackButtonKey>(list);
    }

    public static bool UsesCode(IReadOnlyList<DedicatedActionBinding> bindings, byte code)
    {
        foreach (var binding in bindings)
        {
            foreach (var k in binding.Keys)
                if (k == code) return true;
        }
        return false;
    }

    /// <summary>
    /// Matches a Dedicated Action whose chord contains <paramref name="hardwareCode"/> (such as Quick Menu / OEM
    /// or Xbox / Guide) while all other keys/buttons in that chord are currently held. Prefers longer chords.
    /// </summary>
    public static bool TryMatchHardwareButton(
        IReadOnlyList<DedicatedActionBinding> bindings, byte hardwareCode, out DedicatedAction matched)
    {
        matched = default;
        var bestLength = 0;
        GamepadInput? padState = null;

        foreach (var binding in bindings)
        {
            var keys = binding.Keys;
            if (keys.Count == 0 || !keys.Contains(hardwareCode)) continue;

            var allOtherDown = true;
            var distinctCount = 1; // hardwareCode itself
            for (var i = 0; i < keys.Count; i++)
            {
                var code = keys[i];
                if (code == hardwareCode || code is 0 or BackButtonKey.UnboundCode) continue;

                var duplicate = false;
                for (var j = 0; j < i; j++)
                {
                    if (keys[j] == code)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate) continue;

                distinctCount++;

                if (KeyboardMap.ContainsKey(code))
                {
                    if (!IsKeyboardCodeDown(code))
                    {
                        allOtherDown = false;
                        break;
                    }
                }
                else if (GamepadMap.TryGetValue(code, out var padBit))
                {
                    padState ??= ReadGamepadInputs(includeBackButtons: true);
                    if ((padState.Value & padBit) == 0)
                    {
                        allOtherDown = false;
                        break;
                    }
                }
                else
                {
                    allOtherDown = false;
                    break;
                }
            }

            if (allOtherDown && distinctCount > bestLength)
            {
                bestLength = distinctCount;
                matched = binding.Action;
            }
        }

        return bestLength > 0;
    }

    internal static bool TryGetKeyboard(byte code, out KeySpec spec) => KeyboardMap.TryGetValue(code, out spec);

    internal static bool TryGetGamepad(byte code, out GamepadInput input) => GamepadMap.TryGetValue(code, out input);

    internal static bool TryResolveCodeFromVk(uint vk, bool isExtended, out byte code, out KeySpec spec)
    {
        foreach (var (k, s) in KeyboardMap)
        {
            if (s.Vk != vk && s.AltVk != vk) continue;
            if (s.Extended is { } ext && ext != isExtended) continue;
            code = k;
            spec = s;
            return true;
        }

        code = 0;
        spec = default;
        return false;
    }

    internal static bool IsKeyboardCodeDown(byte code, HashSet<byte>? trackedDown = null)
    {
        if (trackedDown is not null && trackedDown.Contains(code)) return true;
        if (!KeyboardMap.TryGetValue(code, out var spec)) return false;

        if (User32.GetAsyncKeyState((int)spec.Vk) < 0) return true;
        if (spec.AltVk is { } alt && User32.GetAsyncKeyState((int)alt) < 0) return true;
        return false;
    }

    internal static GamepadInput ReadGamepadInputs(bool includeBackButtons = false)
    {
        var result = GamepadInput.None;
        for (uint user = 0; user < XInput.MaxControllers; user++)
        {
            if (!XInput.TryGetState(user, out var state)) continue;

            result |= (GamepadInput)(state.Buttons & 0xF7FF);
            if (state.LeftTrigger >= TriggerThreshold) result |= GamepadInput.Lt;
            if (state.RightTrigger >= TriggerThreshold) result |= GamepadInput.Rt;
        }

        // Fall back to WinMM HID buttons/POV when XInput reports nothing (e.g. under Xbox FSE InputHost),
        // and always read WinMM when back buttons (bits 10/11) are needed.
        var needWinMmFallback = result == GamepadInput.None;
        if (includeBackButtons || needWinMmFallback)
        {
            var info = WinMm.JOYINFOEX.Create();
            for (uint id = 0; id < WinMm.MaxControllers; id++)
            {
                if (WinMm.joyGetPosEx(id, ref info) != WinMm.JOYERR_NOERROR) continue;

                if (includeBackButtons)
                {
                    if ((info.Buttons & (1u << 10)) != 0) result |= GamepadInput.BackLeft;
                    if ((info.Buttons & (1u << 11)) != 0) result |= GamepadInput.BackRight;
                }

                if (needWinMmFallback)
                {
                    if ((info.Buttons & (1u << 0)) != 0) result |= GamepadInput.A;
                    if ((info.Buttons & (1u << 1)) != 0) result |= GamepadInput.B;
                    if ((info.Buttons & (1u << 2)) != 0) result |= GamepadInput.X;
                    if ((info.Buttons & (1u << 3)) != 0) result |= GamepadInput.Y;
                    if ((info.Buttons & (1u << 4)) != 0) result |= GamepadInput.Lb;
                    if ((info.Buttons & (1u << 5)) != 0) result |= GamepadInput.Rb;
                    if ((info.Buttons & (1u << 6)) != 0) result |= GamepadInput.View;
                    if ((info.Buttons & (1u << 7)) != 0) result |= GamepadInput.Menu;
                    if ((info.Buttons & (1u << 8)) != 0) result |= GamepadInput.LsClick;
                    if ((info.Buttons & (1u << 9)) != 0) result |= GamepadInput.RsClick;

                    if (info.Pov < WinMm.JOY_POVCENTERED)
                    {
                        if (info.Pov <= 4500 || info.Pov >= 31500) result |= GamepadInput.DPadUp;
                        if (info.Pov is >= 4500 and <= 13500) result |= GamepadInput.DPadRight;
                        if (info.Pov is >= 13500 and <= 22500) result |= GamepadInput.DPadDown;
                        if (info.Pov is >= 22500 and <= 31500) result |= GamepadInput.DPadLeft;
                    }
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Watches global keyboard shortcuts configured under Settings → Controls &amp; Layout → Dedicated Actions.
/// Supports 1 to 3 simultaneous keys (including mixed keyboard + gamepad chords). The low-level keyboard
/// hook is only installed while at least one dedicated action includes a keyboard key.
/// </summary>
public sealed class DedicatedActionHook : IDisposable
{
    private const nint MaskExtraInfo = 0x5453_554D;   // "TSUM"

    private static readonly Journal Errors = new("errors");

    private sealed record CompiledBinding(
        DedicatedAction Action,
        byte[] KeyboardCodes,
        DedicatedActionKeys.GamepadInput GamepadMask,
        bool HasWinKey);

    private readonly Dispatcher _dispatcher;
    private readonly User32.LowLevelHookProc _proc;
    private readonly HashSet<byte> _downKeys = [];
    private readonly HashSet<DedicatedAction> _latched = [];

    private CompiledBinding[] _bindings = [];
    private nint _hook;
    private uint? _swallowingVk;
    private bool _disposed;

    public event Action<DedicatedAction>? Triggered;

    public DedicatedActionHook(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnKeyboardHook;
    }

    public void SetBindings(IReadOnlyList<DedicatedActionBinding> bindings)
    {
        if (_disposed) return;

        var compiled = Compile(bindings);
        if (_dispatcher.CheckAccess()) ApplyBindings(compiled);
        else _dispatcher.BeginInvoke(() => ApplyBindings(compiled));
    }

    private static CompiledBinding[] Compile(IReadOnlyList<DedicatedActionBinding> bindings)
    {
        var list = new List<CompiledBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var kb = new List<byte>(DedicatedActionKeys.MaxKeys);
            var pad = DedicatedActionKeys.GamepadInput.None;
            var hasWin = false;
            var hasOemButton = false;

            foreach (var code in binding.Keys)
            {
                if (code is 0 or BackButtonKey.UnboundCode) continue;

                if (code == DedicatedActionKeys.OemButtonCode)
                {
                    hasOemButton = true;
                }
                else if (DedicatedActionKeys.TryGetKeyboard(code, out _))
                {
                    if (!kb.Contains(code)) kb.Add(code);
                    if (code == 117) hasWin = true;
                }
                else if (DedicatedActionKeys.TryGetGamepad(code, out var input))
                {
                    pad |= input;
                }
            }

            if (!hasOemButton && kb.Count > 0)
                list.Add(new CompiledBinding(binding.Action, [.. kb], pad, hasWin));
        }

        // Match longer, more specific chords before shorter subsets.
        list.Sort((a, b) => ChordLength(b).CompareTo(ChordLength(a)));
        return [.. list];
    }

    private static int ChordLength(CompiledBinding binding) =>
        binding.KeyboardCodes.Length + BitOperations.PopCount((uint)binding.GamepadMask);

    private static bool IsSubsetOf(CompiledBinding shorter, CompiledBinding longer)
    {
        if ((longer.GamepadMask & shorter.GamepadMask) != shorter.GamepadMask) return false;
        foreach (var k in shorter.KeyboardCodes)
            if (Array.IndexOf(longer.KeyboardCodes, k) < 0) return false;
        return true;
    }

    private void ApplyBindings(CompiledBinding[] bindings)
    {
        if (_disposed) return;
        _bindings = bindings;
        _downKeys.Clear();
        _latched.Clear();
        _swallowingVk = null;

        if (_bindings.Length > 0) Install();
        else Uninstall();
    }

    private void Install()
    {
        if (_hook != 0 || _disposed) return;

        var module = Kernel32.GetModuleHandle(null);
        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == 0)
        {
            var err = Marshal.GetLastWin32Error();
            Errors.Write($"DedicatedActionHook could not install keyboard hook (err={err}).");
        }
    }

    private void Uninstall()
    {
        _downKeys.Clear();
        _latched.Clear();
        _swallowingVk = null;
        var hook = Interlocked.Exchange(ref _hook, 0);
        if (hook != 0) User32.UnhookWindowsHookEx(hook);
    }

    private nint OnKeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code < User32.HC_ACTION || _bindings.Length == 0)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var info = Marshal.PtrToStructure<User32.KBDLLHOOKSTRUCT>(lParam);

        if (info.ExtraInfo == (nuint)MaskExtraInfo || info.VkCode == User32.VK_MENU_MASK)
            return User32.CallNextHookEx(_hook, code, wParam, lParam);

        var message = (uint)wParam;
        var isDown = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        var isUp = message is User32.WM_KEYUP or User32.WM_SYSKEYUP;
        var isExtended = (info.Flags & User32.LLKHF_EXTENDED) != 0;

        if (isUp)
        {
            if (DedicatedActionKeys.TryResolveCodeFromVk(info.VkCode, isExtended, out var upCode, out _))
            {
                _downKeys.Remove(upCode);
                UnlatchFor(upCode);
            }

            if (_swallowingVk == info.VkCode)
            {
                _swallowingVk = null;
                return 1;
            }

            return User32.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (isDown && DedicatedActionKeys.TryResolveCodeFromVk(info.VkCode, isExtended, out var downCode, out var spec))
        {
            _downKeys.Add(downCode);

            if (KeyDropdown.IsAnyOpen)
                return User32.CallNextHookEx(_hook, code, wParam, lParam);

            DedicatedActionKeys.GamepadInput? padState = null;

            foreach (var binding in _bindings)
            {
                if (Array.IndexOf(binding.KeyboardCodes, downCode) < 0) continue;
                if (_latched.Contains(binding.Action))
                {
                    if (!spec.IsModifier && _swallowingVk == info.VkCode) return 1;
                    continue;
                }

                if (!AllKeyboardDown(binding.KeyboardCodes)) continue;

                if (binding.GamepadMask != DedicatedActionKeys.GamepadInput.None)
                {
                    var wantsBack = (binding.GamepadMask & (DedicatedActionKeys.GamepadInput.BackLeft | DedicatedActionKeys.GamepadInput.BackRight)) != 0;
                    padState ??= DedicatedActionKeys.ReadGamepadInputs(includeBackButtons: wantsBack);
                    if ((padState.Value & binding.GamepadMask) != binding.GamepadMask) continue;
                }

                _latched.Add(binding.Action);

                // Suppress any shorter subset chords whose keys are also down so they do not fire alongside this chord.
                foreach (var other in _bindings)
                {
                    if (other.Action != binding.Action && IsSubsetOf(other, binding))
                        _latched.Add(other.Action);
                }

                if (binding.HasWinKey)
                    User32.SendChord(MaskExtraInfo, User32.VK_MENU_MASK);

                var action = binding.Action;
                _dispatcher.BeginInvoke(DispatcherPriority.Send, () => Triggered?.Invoke(action));

                if (!spec.IsModifier)
                {
                    _swallowingVk = info.VkCode;
                    return 1;
                }

                break;
            }
        }

        return User32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool AllKeyboardDown(byte[] codes)
    {
        foreach (var k in codes)
            if (!DedicatedActionKeys.IsKeyboardCodeDown(k, _downKeys)) return false;
        return true;
    }

    private void UnlatchFor(byte releasedCode)
    {
        if (_latched.Count == 0) return;

        foreach (var binding in _bindings)
        {
            if (Array.IndexOf(binding.KeyboardCodes, releasedCode) >= 0)
                _latched.Remove(binding.Action);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
