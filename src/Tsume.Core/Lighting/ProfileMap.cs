namespace Tsume.Core.Lighting;

/// <summary>
/// Where things sit in the controller's 1478-byte profile image.
/// See <c>docs/hardware/controller.md</c>, "Profile memory" and "Lighting".
/// </summary>
public static class ProfileMap
{
    public const int Length = 1478;

    /// <summary>M1 (left back paddle) 8-byte basic key profile.</summary>
    public const int M1ProfileStart = 186;

    /// <summary>M2 (right back paddle) 8-byte basic key profile.</summary>
    public const int M2ProfileStart = 355;

    /// <summary>Length of a single button's basic remap profile (index, flag, turbo, map0..map4).</summary>
    public const int BackButtonProfileLength = 8;

    /// <summary>First byte of the lighting block: the active animation index.</summary>
    public const int LightingStart = 586;

    /// <summary>One past the lighting block. The nine bytes after it (per-button brightness) are never written.</summary>
    public const int LightingEnd = 1469;

    // Animation 0, the one Tsume writes.
    public const int ActiveAnimation = 586;
    public const int KeyframeCount = 587;
    public const int EffectNumber = 588;
    public const int SpeedByte = 589;
    public const int Brightness = 590;
    public const int Keyframes = 591;

    public const int MaxKeyframes = 8;
    public const int Leds = 9;
    public const int KeyframeLength = Leds * 3;

    /// <summary>MSI always writes 9 here.</summary>
    public const byte Effect = 9;

    /// <summary>Header (index, count, effect, speed, brightness) plus all eight keyframes.</summary>
    public const int AnimationLength = 5 + MaxKeyframes * KeyframeLength;
}
