using System.Text;
using Tsume.Core.Controller;
using Tsume.Core.Lighting;

namespace Tsume.App.Platform;

/// <summary>
/// Read-only controller diagnostic: firmware, gamepad mode, the profile image and the decoded
/// lighting block. Run with <c>Tsume.exe --controller-read &lt;file&gt;</c>.
/// </summary>
public static class ControllerReport
{
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        report.AppendLine($"Tsume controller read — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine("Read-only: ReadGamepadMode (0x26) and ReadProfile (0x04). No writes.");
        report.AppendLine();

        using var controller = ClawController.Find();
        if (controller is null)
        {
            report.AppendLine("Controller vendor collection: NOT FOUND");
            return report.ToString();
        }

        report.AppendLine($"Controller vendor collection: found, firmware {controller.FirmwareVersion}");

        var mode = await controller.ReadModeAsync();
        report.AppendLine(mode is { } m ? $"Gamepad mode: {m.Mode} (M keys {m.MKeys})" : "Gamepad mode: no answer");
        report.AppendLine();

        var started = DateTime.UtcNow;
        var profile = await controller.ReadProfileRangeAsync(0, ProfileMap.Length);
        var took = DateTime.UtcNow - started;

        if (profile is null)
        {
            report.AppendLine("Profile: a chunk went unanswered");
            return report.ToString();
        }

        report.AppendLine($"Profile: {profile.Length} bytes in {took.TotalMilliseconds:0} ms");
        report.AppendLine();

        for (var row = 0; row < profile.Length; row += 16)
        {
            var count = Math.Min(16, profile.Length - row);
            report.AppendLine($"{row,5}  {Convert.ToHexString(profile, row, count).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b)}");
        }

        report.AppendLine();
        report.AppendLine("Lighting block, animation 0:");
        report.AppendLine($"  active animation  {profile[ProfileMap.ActiveAnimation]}");
        report.AppendLine($"  keyframes         {profile[ProfileMap.KeyframeCount]}");
        report.AppendLine($"  effect number     {profile[ProfileMap.EffectNumber]}   (MSI always writes 9)");
        report.AppendLine($"  speed byte        {profile[ProfileMap.SpeedByte]}");
        report.AppendLine($"  brightness        {profile[ProfileMap.Brightness]}");

        for (var f = 0; f < Math.Min((int)profile[ProfileMap.KeyframeCount], ProfileMap.MaxKeyframes); f++)
        {
            var at = ProfileMap.Keyframes + f * ProfileMap.KeyframeLength;
            report.AppendLine($"  frame {f}           {Convert.ToHexString(profile, at, ProfileMap.KeyframeLength)}");
        }

        var decoded = StickLighting.Decode(profile.AsSpan(ProfileMap.ActiveAnimation, ProfileMap.AnimationLength));
        report.AppendLine();
        report.AppendLine($"Decoded: {decoded}");

        // Read the lighting block again to check that reads are stable.
        var again = await controller.ReadProfileRangeAsync(ProfileMap.LightingStart, ProfileMap.LightingEnd - ProfileMap.LightingStart);
        report.AppendLine(again is null
            ? "Second read of the lighting block: no answer"
            : $"Second read of the lighting block: {(again.AsSpan().SequenceEqual(profile.AsSpan(ProfileMap.LightingStart, again.Length)) ? "identical" : "DIFFERENT")}");

        return report.ToString();
    }

    /// <summary>
    /// Writes steady blue to working memory, reads it back, then restores the original bytes.
    /// Never saves to flash. Run with <c>Tsume.exe --lighting-check &lt;file&gt;</c>.
    /// </summary>
    public static async Task<string> WriteCheckAsync()
    {
        var report = new StringBuilder();
        report.AppendLine($"Tsume lighting write check — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine("Writes animation 0 of the lighting block to RAM (0x21), restores it. No SyncToROM (0x22).");
        report.AppendLine();

        using var controller = ClawController.Find(ClawController.MsiVendorId, ClawController.MsiProductIds);
        if (controller is null) return report.AppendLine("MSI HJC controller: NOT FOUND").ToString();

        const int start = ProfileMap.ActiveAnimation;
        const int length = ProfileMap.AnimationLength;

        var original = await controller.ReadProfileRangeAsync(start, length);
        if (original is null) return report.AppendLine("Original read: no answer — stopped before writing").ToString();
        report.AppendLine($"Original ({length} bytes): {Convert.ToHexString(original)}");

        var blue = new StickLighting(LightingEffect.Steady, LightingPalette.Swatches[5], LightingPalette.Swatches[5],
            LightingPalette.Swatches[8], 100, 100, 5).Encode();

        var started = DateTime.UtcNow;
        var wrote = await controller.WriteLightingAsync(start, blue);
        report.AppendLine($"Write steady blue ({blue.Length} bytes): {(wrote ? "acknowledged" : "NOT acknowledged")} in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");

        var back = await controller.ReadProfileRangeAsync(start, blue.Length);
        report.AppendLine($"Read back: {(back is null ? "no answer" : back.AsSpan().SequenceEqual(blue) ? "matches" : "DIFFERS: " + Convert.ToHexString(back))}");

        await Task.Delay(TimeSpan.FromSeconds(4));

        var restored = await controller.WriteLightingAsync(start, original);
        var final = await controller.ReadProfileRangeAsync(start, length);
        report.AppendLine($"Restore: {(restored ? "acknowledged" : "NOT acknowledged")}; read back {(final is null ? "no answer" : final.AsSpan().SequenceEqual(original) ? "matches the original" : "DIFFERS: " + Convert.ToHexString(final))}");

        return report.ToString();
    }

    /// <summary>
    /// Switches to the other pad mode and back, logging the mode and what Windows sees of the pad
    /// at each stage. Run with <c>Tsume.exe --pad-mode-check &lt;file&gt;</c>.
    /// </summary>
    public static async Task<string> ModeCheckAsync()
    {
        var report = new StringBuilder();
        report.AppendLine($"Tsume pad mode check — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine("SwitchMode (0x24) to the other of gamepad (1) / cursor (4), then back. No profile writes, no SyncToROM.");
        report.AppendLine();

        void Seen(string when)
        {
            report.AppendLine($"{when}:");
            foreach (var path in Interop.Hid.DevicePaths().Where(ClawController.IsClaw))
                report.AppendLine($"  HID  {path}");

            var slots = Enumerable.Range(0, Interop.XInput.MaxControllers)
                .Where(slot => Interop.XInput.XInputGetState((uint)slot, out _) == Interop.XInput.ERROR_SUCCESS);
            report.AppendLine($"  XInput slots answering: {string.Join(", ", slots.DefaultIfEmpty(-1)).Replace("-1", "none")}");
            report.AppendLine();
        }

        Seen("Before");

        if (await ControllerMode.ReadAsync() is not { } original)
            return report.AppendLine("Mode read: no answer — stopped before switching").ToString();

        var other = original == PadMode.Gamepad ? PadMode.Cursor : PadMode.Gamepad;
        report.AppendLine($"Starting mode: {original}");
        report.AppendLine();

        try
        {
            report.AppendLine($"Switch to {other}:");
            var there = await ControllerMode.SetAsync(other, line => report.AppendLine("  " + line));
            report.AppendLine($"  => {(there ? "confirmed" : "NOT confirmed")}");
            report.AppendLine();

            Seen($"In {other}");
            await Task.Delay(TimeSpan.FromSeconds(3));
            Seen($"In {other}, three seconds on");
        }
        finally
        {
            report.AppendLine($"Back to {original}:");
            var back = await ControllerMode.SetAsync(original, line => report.AppendLine("  " + line));
            report.AppendLine($"  => {(back ? "confirmed" : "NOT confirmed")}");
            report.AppendLine();

            await Task.Delay(TimeSpan.FromSeconds(1));
            Seen("After");
            report.AppendLine($"Mode at the end: {(await ControllerMode.ReadAsync())?.ToString() ?? "no answer"}");
        }

        return report.ToString();
    }
}
