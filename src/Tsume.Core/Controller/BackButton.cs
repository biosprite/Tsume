using System.Collections.ObjectModel;

namespace Tsume.Core.Controller;

/// <summary>
/// One key or button code a controller's firmware understands in its button-remap table.
/// The codes are the ones the MSI Claw controller uses (<c>docs/hardware/controller.md</c>).
/// </summary>
public sealed record BackButtonKey(byte Code, string Name, string Category)
{
    public const byte UnboundCode = 0xFF;
    public const byte F22Code = 157;

    public static readonly BackButtonKey Unbound = new(UnboundCode, "None (Unbound)", "");

    /// <summary>Every key code supported by the Claw controller, ordered for browsing in a dropdown.</summary>
    public static IReadOnlyList<BackButtonKey> All { get; } = new ReadOnlyCollection<BackButtonKey>(
    [
        Unbound,

        // Modifiers
        new(116, "Left Ctrl", "Modifier"),
        new(104, "Left Shift", "Modifier"),
        new(118, "Left Alt", "Modifier"),
        new(117, "Win", "Modifier"),
        new(121, "Right Ctrl", "Modifier"),
        new(115, "Right Shift", "Modifier"),
        new(120, "Right Alt", "Modifier"),

        // Common / Navigation & Editing
        new(50, "Esc", "Keyboard"),
        new(103, "Enter", "Keyboard"),
        new(119, "Space", "Keyboard"),
        new(77, "Tab", "Keyboard"),
        new(76, "Backspace", "Keyboard"),
        new(122, "Insert", "Keyboard"),
        new(125, "Delete", "Keyboard"),
        new(123, "Home", "Keyboard"),
        new(126, "End", "Keyboard"),
        new(124, "Page Up", "Keyboard"),
        new(127, "Page Down", "Keyboard"),
        new(91, "Caps Lock", "Keyboard"),

        // Arrow keys
        new(128, "Up Arrow", "Keyboard"),
        new(129, "Down Arrow", "Keyboard"),
        new(130, "Left Arrow", "Keyboard"),
        new(131, "Right Arrow", "Keyboard"),

        // Letters A-Z
        new(92, "A", "Keyboard"),
        new(109, "B", "Keyboard"),
        new(107, "C", "Keyboard"),
        new(94, "D", "Keyboard"),
        new(80, "E", "Keyboard"),
        new(95, "F", "Keyboard"),
        new(96, "G", "Keyboard"),
        new(97, "H", "Keyboard"),
        new(85, "I", "Keyboard"),
        new(98, "J", "Keyboard"),
        new(99, "K", "Keyboard"),
        new(100, "L", "Keyboard"),
        new(111, "M", "Keyboard"),
        new(110, "N", "Keyboard"),
        new(86, "O", "Keyboard"),
        new(87, "P", "Keyboard"),
        new(78, "Q", "Keyboard"),
        new(81, "R", "Keyboard"),
        new(93, "S", "Keyboard"),
        new(82, "T", "Keyboard"),
        new(84, "U", "Keyboard"),
        new(108, "V", "Keyboard"),
        new(79, "W", "Keyboard"),
        new(106, "X", "Keyboard"),
        new(83, "Y", "Keyboard"),
        new(105, "Z", "Keyboard"),

        // Number row
        new(64, "1", "Keyboard"),
        new(65, "2", "Keyboard"),
        new(66, "3", "Keyboard"),
        new(67, "4", "Keyboard"),
        new(68, "5", "Keyboard"),
        new(69, "6", "Keyboard"),
        new(70, "7", "Keyboard"),
        new(71, "8", "Keyboard"),
        new(72, "9", "Keyboard"),
        new(73, "0", "Keyboard"),

        // Function keys
        new(51, "F1", "Function"),
        new(52, "F2", "Function"),
        new(53, "F3", "Function"),
        new(54, "F4", "Function"),
        new(55, "F5", "Function"),
        new(56, "F6", "Function"),
        new(57, "F7", "Function"),
        new(58, "F8", "Function"),
        new(59, "F9", "Function"),
        new(60, "F10", "Function"),
        new(61, "F11", "Function"),
        new(62, "F12", "Function"),
        new(157, "F22", "Function"),
        new(156, "F23", "Function"),

        // Punctuation & symbols
        new(63, "` (Grave)", "Symbol"),
        new(74, "- (Minus)", "Symbol"),
        new(75, "= (Equals)", "Symbol"),
        new(88, "[ (Left Bracket)", "Symbol"),
        new(89, "] (Right Bracket)", "Symbol"),
        new(90, "\\ (Backslash)", "Symbol"),
        new(101, "; (Semicolon)", "Symbol"),
        new(102, "' (Quote)", "Symbol"),
        new(112, ", (Comma)", "Symbol"),
        new(113, ". (Period)", "Symbol"),
        new(114, "/ (Slash)", "Symbol"),

        // Numpad
        new(132, "Num Lock", "Numpad"),
        new(139, "Numpad 0", "Numpad"),
        new(140, "Numpad 1", "Numpad"),
        new(141, "Numpad 2", "Numpad"),
        new(142, "Numpad 3", "Numpad"),
        new(143, "Numpad 4", "Numpad"),
        new(144, "Numpad 5", "Numpad"),
        new(145, "Numpad 6", "Numpad"),
        new(146, "Numpad 7", "Numpad"),
        new(147, "Numpad 8", "Numpad"),
        new(148, "Numpad 9", "Numpad"),
        new(133, "Numpad /", "Numpad"),
        new(134, "Numpad *", "Numpad"),
        new(135, "Numpad -", "Numpad"),
        new(136, "Numpad +", "Numpad"),
        new(137, "Numpad .", "Numpad"),
        new(138, "Numpad Enter", "Numpad"),

        // Media keys
        new(149, "Play / Pause", "Media"),
        new(150, "Stop", "Media"),
        new(151, "Next Track", "Media"),
        new(152, "Previous Track", "Media"),
        new(153, "Volume Up", "Media"),
        new(154, "Volume Down", "Media"),
        new(155, "Mute", "Media"),

        // Mouse
        new(200, "Left Click", "Mouse"),
        new(202, "Right Click", "Mouse"),
        new(201, "Middle Click", "Mouse"),
        new(203, "Mouse Back (Btn 4)", "Mouse"),
        new(204, "Mouse Forward (Btn 5)", "Mouse"),
        new(205, "Scroll Up", "Mouse"),
        new(206, "Scroll Down", "Mouse"),

        // Gamepad buttons & axes
        new(9, "A", "Gamepad"),
        new(10, "B", "Gamepad"),
        new(11, "X", "Gamepad"),
        new(12, "Y", "Gamepad"),
        new(5, "LB", "Gamepad"),
        new(6, "RB", "Gamepad"),
        new(19, "LT", "Gamepad"),
        new(20, "RT", "Gamepad"),
        new(7, "LS Click", "Gamepad"),
        new(8, "RS Click", "Gamepad"),
        new(14, "View", "Gamepad"),
        new(15, "Menu", "Gamepad"),
        new(13, "Xbox / Home", "Gamepad"),
        new(16, "Share / Camera", "Gamepad"),
        new(17, "Back Left (M1)", "Gamepad"),
        new(18, "Back Right (M2)", "Gamepad"),
        new(1, "D-Pad Up", "Gamepad"),
        new(2, "D-Pad Down", "Gamepad"),
        new(3, "D-Pad Left", "Gamepad"),
        new(4, "D-Pad Right", "Gamepad"),
        new(29, "D-Pad Up-Left", "Gamepad"),
        new(30, "D-Pad Up-Right", "Gamepad"),
        new(31, "D-Pad Down-Left", "Gamepad"),
        new(32, "D-Pad Down-Right", "Gamepad"),
        new(21, "Left Stick Up", "Gamepad"),
        new(22, "Left Stick Down", "Gamepad"),
        new(23, "Left Stick Left", "Gamepad"),
        new(24, "Left Stick Right", "Gamepad"),
        new(33, "Left Stick Up-Left", "Gamepad"),
        new(34, "Left Stick Up-Right", "Gamepad"),
        new(35, "Left Stick Down-Left", "Gamepad"),
        new(36, "Left Stick Down-Right", "Gamepad"),
        new(25, "Right Stick Up", "Gamepad"),
        new(26, "Right Stick Down", "Gamepad"),
        new(27, "Right Stick Left", "Gamepad"),
        new(28, "Right Stick Right", "Gamepad"),
        new(37, "Right Stick Up-Left", "Gamepad"),
        new(38, "Right Stick Up-Right", "Gamepad"),
        new(39, "Right Stick Down-Left", "Gamepad"),
        new(40, "Right Stick Down-Right", "Gamepad"),
    ]);

    /// <summary>HJC key table without MSI-specific <c>F22</c> (<c>157</c>), which is absent from Acer's HJC core.</summary>
    public static IReadOnlyList<BackButtonKey> AllWithoutF22 { get; } =
        Array.AsReadOnly(All.Where(key => key.Code != F22Code).ToArray());

    private static readonly Dictionary<byte, BackButtonKey> Lookup = All.ToDictionary(key => key.Code);

    public static BackButtonKey FromCode(byte code) =>
        Lookup.TryGetValue(code, out var found) ? found : new BackButtonKey(code, $"Key 0x{code:X2}", "Other");

    public override string ToString() => Name;
}

/// <summary>
/// A back button's simultaneous key binding (0 to <see cref="MaxKeys"/> keys held together) and its controller encoding.
/// MSI Claw layout: <c>[index=1, flag=0x04, turboSpeed, map0, map1, map2, map3, map4]</c>.
/// </summary>
public sealed record BackButtonBinding(IReadOnlyList<byte> Keys, byte TurboSpeed = 0x0C, bool IsMacro = false)
{
    public const int DefaultBlockLength = 8;
    public const int MaxKeys = 3;
    private const byte RemapIndex = 0x01;
    private const byte MacroIndex = 0x02;
    private const byte MapOrMacroFlag = 0x04;
    private const byte DefaultTurboSpeed = 0x0C;

    public static BackButtonBinding Empty { get; } = new([]);

    public static BackButtonBinding Decode(ReadOnlySpan<byte> raw, int maxKeys = MaxKeys)
    {
        if (raw.Length < 4) return Empty;

        var turbo = raw[2] == 0 ? DefaultTurboSpeed : raw[2];
        if (raw[0] == MacroIndex) return new([], turbo, IsMacro: true);

        var limit = Math.Clamp(maxKeys, 1, raw.Length - 3);
        var keys = new List<byte>(limit);
        for (var i = 3; i < raw.Length && keys.Count < limit; i++)
        {
            var code = raw[i];
            if (code is 0 or BackButtonKey.UnboundCode) continue;
            keys.Add(code);
        }

        return new BackButtonBinding(keys, turbo);
    }

    public byte[] Encode(int blockLength = DefaultBlockLength, int maxKeys = MaxKeys)
    {
        var length = Math.Max(blockLength, 4);
        var limit = Math.Clamp(maxKeys, 1, length - 3);

        var bytes = new byte[length];
        bytes[0] = RemapIndex;
        bytes[1] = MapOrMacroFlag;
        bytes[2] = TurboSpeed == 0 ? DefaultTurboSpeed : TurboSpeed;

        var count = 0;
        foreach (var code in Keys)
        {
            if (code is 0 or BackButtonKey.UnboundCode) continue;
            if (count >= limit) break;
            bytes[3 + count++] = code;
        }

        if (count == 0) bytes[3] = BackButtonKey.UnboundCode;
        return bytes;
    }

    public string FormatSummary(string unboundLabel, string? macroLabel = null, int maxKeys = MaxKeys)
    {
        var active = Keys.Where(code => code is not (0 or BackButtonKey.UnboundCode)).Take(maxKeys).ToList();
        if (active.Count > 0)
            return string.Join(" + ", active.Select(code => BackButtonKey.FromCode(code).Name));

        return IsMacro && !string.IsNullOrEmpty(macroLabel) ? macroLabel : unboundLabel;
    }
}
