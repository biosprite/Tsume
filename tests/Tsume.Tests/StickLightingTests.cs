using Tsume.Core.Lighting;

namespace Tsume.Tests;

public sealed class StickLightingTests
{
    [Theory]
    [InlineData(LightingEffect.Steady)]
    [InlineData(LightingEffect.Breathe)]
    [InlineData(LightingEffect.Rainbow)]
    [InlineData(LightingEffect.Spin)]
    public void Encode_TogglingButtonsLit_DoesNotChangeGlobalBrightnessOrStickLeds(LightingEffect effect)
    {
        var buttonsOn = new StickLighting(
            effect,
            LeftStick: new Rgb(0, 96, 255),
            RightStick: new Rgb(255, 80, 0),
            Buttons: new Rgb(0, 255, 255),
            StickBrightness: 30,
            ButtonBrightness: 100,
            Speed: 2,
            SticksLit: true,
            ButtonsLit: true,
            ButtonsSteady: false);

        var buttonsOff = buttonsOn with { ButtonsLit = false };

        var onBytes = buttonsOn.Encode();
        var offBytes = buttonsOff.Encode();

        Assert.Equal(onBytes.Length, offBytes.Length);
        Assert.Equal(100, onBytes[4]);
        Assert.Equal(100, offBytes[4]);

        // Header (bytes 0..4) and all stick LEDs (LEDs 0..7 in every keyframe) must stay identical.
        Assert.Equal(onBytes[..5], offBytes[..5]);

        var frameCount = onBytes[1];
        for (var f = 0; f < frameCount; f++)
        {
            var frameStart = 5 + f * ProfileMap.KeyframeLength;
            var stickLength = 8 * 3;
            Assert.Equal(
                onBytes[frameStart..(frameStart + stickLength)],
                offBytes[frameStart..(frameStart + stickLength)]);

            // Button LED (LED 8) must be black when off.
            var buttonStart = frameStart + stickLength;
            Assert.Equal(new byte[] { 0, 0, 0 }, offBytes[buttonStart..(buttonStart + 3)]);
        }
    }

    [Theory]
    [InlineData(LightingEffect.Steady)]
    [InlineData(LightingEffect.Breathe)]
    [InlineData(LightingEffect.Rainbow)]
    [InlineData(LightingEffect.Spin)]
    public void Encode_TogglingSticksLit_DoesNotChangeGlobalBrightnessOrButtonLed(LightingEffect effect)
    {
        var sticksOn = new StickLighting(
            effect,
            LeftStick: new Rgb(0, 96, 255),
            RightStick: new Rgb(0, 96, 255),
            Buttons: new Rgb(255, 255, 255),
            StickBrightness: 100,
            ButtonBrightness: 25,
            Speed: 4,
            SticksLit: true,
            ButtonsLit: true,
            ButtonsSteady: false);

        var sticksOff = sticksOn with { SticksLit = false };

        var onBytes = sticksOn.Encode();
        var offBytes = sticksOff.Encode();

        Assert.Equal(onBytes[..5], offBytes[..5]);
        Assert.Equal(100, onBytes[4]);

        var frameCount = onBytes[1];
        for (var f = 0; f < frameCount; f++)
        {
            var frameStart = 5 + f * ProfileMap.KeyframeLength;
            var buttonStart = frameStart + 8 * 3;
            Assert.Equal(
                onBytes[buttonStart..(buttonStart + 3)],
                offBytes[buttonStart..(buttonStart + 3)]);
        }
    }

    [Fact]
    public void Encode_ChangingButtonBrightness_LeavesStickLedsUnchanged()
    {
        var low = new StickLighting(
            LightingEffect.Steady,
            LightingPalette.Swatches[5],
            LightingPalette.Swatches[5],
            LightingPalette.Swatches[8],
            StickBrightness: 30,
            ButtonBrightness: 20,
            Speed: 5);

        var high = low with { ButtonBrightness = 100 };

        var lowBytes = low.Encode();
        var highBytes = high.Encode();

        Assert.Equal(100, lowBytes[4]);
        Assert.Equal(100, highBytes[4]);
        Assert.Equal(lowBytes[..(5 + 8 * 3)], highBytes[..(5 + 8 * 3)]);
    }

    [Fact]
    public void EncodeDecode_RoundTripsAllBrightnessLevelsExactly()
    {
        for (var brightness = 1; brightness <= 100; brightness++)
        {
            var setup = new StickLighting(
                LightingEffect.Steady,
                LightingPalette.Swatches[5],
                LightingPalette.Swatches[5],
                LightingPalette.Swatches[8],
                StickBrightness: brightness,
                ButtonBrightness: 101 - brightness,
                Speed: 5);

            var decoded = StickLighting.Decode(setup.Encode());

            Assert.Equal(LightingEffect.Steady, decoded.Effect);
            Assert.Equal(brightness, decoded.StickBrightness);
            Assert.Equal(101 - brightness, decoded.ButtonBrightness);
            Assert.True(decoded.SticksLit);
            Assert.True(decoded.ButtonsLit);
        }
    }

    [Fact]
    public void Decode_StillReadsLegacyScaledGlobalBrightness()
    {
        // Simulates a profile written with global brightness 30 and full-scale stick RGB (00 60 FF), buttons off.
        var payload = new byte[5 + ProfileMap.KeyframeLength];
        payload[0] = 0;
        payload[1] = 1;
        payload[2] = ProfileMap.Effect;
        payload[3] = StickLighting.SpeedToByte(2);
        payload[4] = 30;
        for (var led = 0; led < 8; led++)
        {
            var at = 5 + led * 3;
            payload[at] = 0x00;
            payload[at + 1] = 0x60;
            payload[at + 2] = 0xFF;
        }

        var decoded = StickLighting.Decode(payload);

        Assert.Equal(LightingEffect.Steady, decoded.Effect);
        Assert.Equal(30, decoded.StickBrightness);
        Assert.True(decoded.SticksLit);
        Assert.False(decoded.ButtonsLit);
        Assert.Equal(new Rgb(0x00, 0x60, 0xFF), decoded.LeftStick);
    }

    [Fact]
    public void Encode_OffEffect_UsesZeroBrightnessAndAllBlack()
    {
        var off = StickLighting.Default with { Effect = LightingEffect.Off };

        var bytes = off.Encode();

        Assert.Equal(0, bytes[4]);
        Assert.All(bytes[5..], b => Assert.Equal(0, b));
        Assert.Equal(LightingEffect.Off, StickLighting.Decode(bytes).Effect);
    }
}
