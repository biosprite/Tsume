namespace Tsume.Core.Lighting;

/// <summary>A colour as the controller takes it: one byte per channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public Rgb Scaled(double factor) => new(
        (byte)Math.Round(R * factor), (byte)Math.Round(G * factor), (byte)Math.Round(B * factor));

    /// <summary>Hue in degrees, full saturation and value.</summary>
    public static Rgb FromHue(double hue)
    {
        hue = ((hue % 360) + 360) % 360;
        var x = 1 - Math.Abs(hue / 60 % 2 - 1);

        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };

        return new((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    /// <summary>A hue mixed with white: saturation 1 is the pure hue, 0 is white. Always at full value.</summary>
    public static Rgb FromHue(double hue, double saturation)
    {
        var pure = FromHue(hue);
        saturation = Math.Clamp(saturation, 0, 1);

        byte Channel(byte full) => (byte)Math.Round(255 - (255 - full) * saturation);
        return new(Channel(pure.R), Channel(pure.G), Channel(pure.B));
    }

    /// <summary>Hue in degrees, 0–360. Greys return 0.</summary>
    public double Hue
    {
        get
        {
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            if (max == min) return 0;

            var spread = max - min;
            var sixth = max == R ? (G - B) / spread % 6
                : max == G ? (B - R) / spread + 2
                : (R - G) / spread + 4;

            return (sixth * 60 + 360) % 360;
        }
    }

    /// <summary>How far from white, 0–1.</summary>
    public double Saturation
    {
        get
        {
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            return max == 0 ? 0 : (max - min) / max;
        }
    }

    public int DistanceTo(Rgb other) =>
        (R - other.R) * (R - other.R) + (G - other.G) * (G - other.G) + (B - other.B) * (B - other.B);
}

/// <summary>What the lights do. The controller only knows keyframes; each effect is a way of filling them.</summary>
public enum LightingEffect
{
    /// <summary>Lights out.</summary>
    Off,

    /// <summary>One keyframe: each ring holds its colour.</summary>
    Steady,

    /// <summary>Two keyframes, colour then black: the rings fade in and out.</summary>
    Breathe,

    /// <summary>Six keyframes around the colour wheel, shifted LED by LED so it swirls. Ignores the chosen colours.</summary>
    Rainbow,

    /// <summary>Four keyframes, one LED of each ring lit brightest in turn.</summary>
    Spin,
}

/// <summary>The whole lighting setup, and its encoding to and from the controller's profile bytes.</summary>
public sealed record StickLighting(
    LightingEffect Effect,
    Rgb LeftStick,
    Rgb RightStick,
    Rgb Buttons,
    int StickBrightness,
    int ButtonBrightness,
    int Speed,
    bool SticksLit = true,    // off: the rings around the sticks stay dark
    bool ButtonsLit = true,   // off: the face buttons stay dark
    bool ButtonsSteady = false)   // the face buttons hold their colour while the sticks play the effect
{
    public const int MinSpeed = 1;
    public const int MaxSpeed = 10;

    /// <summary>Used until the controller has been read.</summary>
    public static readonly StickLighting Default = new(
        LightingEffect.Steady, LightingPalette.Swatches[5], LightingPalette.Swatches[5], LightingPalette.Swatches[8], 100, 100, 5);

    /// <summary>
    /// The controller has a single brightness. It is set to the brighter group's,
    /// and the dimmer group's colours are darkened to make up the difference.
    /// </summary>
    private int TopBrightness => Effect == LightingEffect.Off ? 0 : Math.Clamp(
        Math.Max(SticksLit ? StickBrightness : 0, ButtonsLit ? ButtonBrightness : 0), 0, 100);

    /// <summary>How much of the controller's brightness an LED's group gets, 0–1.</summary>
    private double Share(int led)
    {
        var (lit, brightness) = led == ButtonsLed ? (ButtonsLit, ButtonBrightness) : (SticksLit, StickBrightness);
        return lit && TopBrightness > 0 ? Math.Clamp((double)brightness / TopBrightness, 0, 1) : 0;
    }

    /// <summary>LED numbers: left ring 0–3, right ring 4–7, the face buttons 8.</summary>
    private const int LeftFirst = 0, RightFirst = 4, ButtonsLed = 8, RingSize = 4;

    /// <summary>In Spin, the brightness of each ring's unlit LEDs relative to the lit one.</summary>
    private const double SpinTrail = 0.08;

    /// <summary>
    /// Animation 0 of the lighting block, from <see cref="ProfileMap.ActiveAnimation"/>:
    /// the header and only the keyframes in use (the rest are left as they are).
    /// </summary>
    public byte[] Encode()
    {
        var frames = Keyframes();
        var bytes = new byte[5 + frames.Count * ProfileMap.KeyframeLength];

        bytes[0] = 0;                               // play animation 0
        bytes[1] = (byte)frames.Count;
        bytes[2] = ProfileMap.Effect;
        bytes[3] = SpeedToByte(Speed);
        bytes[4] = (byte)TopBrightness;

        for (var f = 0; f < frames.Count; f++)
        for (var led = 0; led < ProfileMap.Leds; led++)
        {
            var at = 5 + f * ProfileMap.KeyframeLength + led * 3;
            var colour = frames[f][led];
            bytes[at] = colour.R;
            bytes[at + 1] = colour.G;
            bytes[at + 2] = colour.B;
        }

        return bytes;
    }

    /// <summary>
    /// The controller's speed byte counts down (MSI writes 20 − speed). Tsume's ten steps map to
    /// 19 (slowest) down to 1 (fastest), avoiding 0 and 20, which MSI never writes.
    /// </summary>
    public static byte SpeedToByte(int speed) => (byte)(21 - 2 * Math.Clamp(speed, MinSpeed, MaxSpeed));

    public static int SpeedFromByte(byte value) => Math.Clamp((22 - value) / 2, MinSpeed, MaxSpeed);

    private List<Rgb[]> Keyframes() => Effect switch
    {
        LightingEffect.Off => [Frame(_ => Rgb.Black)],
        LightingEffect.Steady => [Frame(ColourOf)],
        LightingEffect.Breathe => [Frame(ColourOf), Frame(_ => Rgb.Black)],
        LightingEffect.Rainbow => [.. Enumerable.Range(0, 6).Select(f => Frame(led => Rgb.FromHue((f + RingPosition(led)) * 60)))],
        LightingEffect.Spin => [.. Enumerable.Range(0, RingSize).Select(f => Frame(led =>
            led == ButtonsLed || RingPosition(led) == f ? ColourOf(led) : ColourOf(led).Scaled(SpinTrail)))],
        _ => [Frame(ColourOf)],
    };

    private Rgb[] Frame(Func<int, Rgb> colourOf) =>
        [.. Enumerable.Range(0, ProfileMap.Leds).Select(led =>
            (ButtonsSteady && led == ButtonsLed ? Buttons : colourOf(led)).Scaled(Share(led)))];

    private Rgb ColourOf(int led) => led switch
    {
        < RightFirst => LeftStick,
        < ButtonsLed => RightStick,
        _ => Buttons,
    };

    /// <summary>Where an LED sits around its ring, 0–3; the face buttons count as 0.</summary>
    private static int RingPosition(int led) => led == ButtonsLed ? 0 : (led - (led < RightFirst ? LeftFirst : RightFirst));

    /// <summary>
    /// Reads animation 0 from the bytes at <see cref="ProfileMap.ActiveAnimation"/>. Effects are recognised
    /// by keyframe count, so a setup not written by Tsume or MSI may come back approximated.
    /// </summary>
    public static StickLighting Decode(ReadOnlySpan<byte> bytes)
    {
        var animation = bytes.ToArray();
        if (animation.Length < 5 + ProfileMap.KeyframeLength) return Default;

        var count = Math.Clamp((int)animation[1], 1, ProfileMap.MaxKeyframes);
        var brightness = Math.Min((int)animation[4], 100);
        var speed = SpeedFromByte(animation[3]);

        Rgb Led(int frame, int led)
        {
            var at = 5 + frame * ProfileMap.KeyframeLength + led * 3;
            return at + 2 < animation.Length ? new Rgb(animation[at], animation[at + 1], animation[at + 2]) : Rgb.Black;
        }

        // In Spin the brightest LED moves; frame 0's first LED of each ring is the lit one.
        var left = Led(0, LeftFirst);
        var right = Led(0, RightFirst);
        var buttons = Led(0, ButtonsLed);

        var allBlack = Enumerable.Range(0, count).All(f =>
            Enumerable.Range(0, ProfileMap.Leds).All(led => Led(f, led) == Rgb.Black));

        var effect = brightness == 0 || allBlack ? LightingEffect.Off
            : count switch
            {
                2 => LightingEffect.Breathe,
                4 => LightingEffect.Spin,
                6 => LightingEffect.Rainbow,
                _ => LightingEffect.Steady,
            };

        // When off, keep the default colours so there is something to turn back on.
        if (effect == LightingEffect.Off)
            return Default with { Effect = LightingEffect.Off, Speed = speed };

        // A group that is black in every keyframe is switched off; it gets the default colour.
        var sticksDark = Enumerable.Range(0, count).All(f =>
            Enumerable.Range(0, ButtonsLed).All(led => Led(f, led) == Rgb.Black));
        var buttonsDark = Enumerable.Range(0, count).All(f => Led(f, ButtonsLed) == Rgb.Black);

        // In Breathe and Rainbow the buttons change between keyframes unless ButtonsSteady is set.
        var buttonsSteady = !buttonsDark
            && effect is LightingEffect.Breathe or LightingEffect.Rainbow
            && Enumerable.Range(1, count - 1).All(f => Led(f, ButtonsLed) == buttons);

        // A dimmer group was written as darker colours: the strongest channel gives its
        // brightness, and scaling that channel to 255 recovers the colour.
        static int Strongest(Rgb c) => Math.Max(c.R, Math.Max(c.G, c.B));
        static Rgb AtFull(Rgb c) => Strongest(c) is var s and > 0 ? c.Scaled(255.0 / s) : c;
        int Level(Rgb c) => Math.Clamp((int)Math.Round(brightness * Strongest(c) / 255.0), 1, 100);

        return new StickLighting(
            effect,
            sticksDark ? Default.LeftStick : AtFull(left),
            sticksDark ? Default.RightStick : AtFull(right),
            buttonsDark ? Default.Buttons : AtFull(buttons),
            sticksDark ? Default.StickBrightness : Math.Max(Level(left), Level(right)),
            buttonsDark ? Default.ButtonBrightness : Level(buttons),
            speed, !sticksDark, !buttonsDark, buttonsSteady);
    }
}

/// <summary>The preset colours offered on the lighting screen, in rainbow order.</summary>
public static class LightingPalette
{
    public static readonly IReadOnlyList<Rgb> Swatches =
    [
        new(0xFF, 0x00, 0x00),   // red
        new(0xFF, 0x50, 0x00),   // orange
        new(0xFF, 0xC8, 0x00),   // yellow
        new(0x00, 0xFF, 0x20),   // green
        new(0x00, 0xFF, 0xD0),   // teal
        new(0x00, 0x60, 0xFF),   // blue
        new(0x80, 0x00, 0xFF),   // purple
        new(0xFF, 0x00, 0x90),   // pink
        new(0xFF, 0xFF, 0xFF),   // white
    ];

    /// <summary>The swatch closest to <paramref name="colour"/>.</summary>
    public static int Nearest(Rgb colour)
    {
        var best = 0;
        for (var i = 1; i < Swatches.Count; i++)
            if (Swatches[i].DistanceTo(colour) < Swatches[best].DistanceTo(colour)) best = i;
        return best;
    }

    /// <summary>
    /// Snaps a colour read back from the controller to a swatch if it is very close to one.
    /// A dimmed group is stored as darker colours and comes back slightly off.
    /// </summary>
    public static Rgb Snap(Rgb colour)
    {
        const int close = 12 * 12;   // the nearest two swatches are 80 apart

        var nearest = Swatches[Nearest(colour)];
        return nearest.DistanceTo(colour) <= close ? nearest : colour;
    }
}
