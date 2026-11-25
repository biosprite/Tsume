using Tsume.Core.Controller;
using Tsume.Core.Devices;

namespace Tsume.App.Platform;

/// <summary>Protocol-agnostic controller handle for rear back-button remapping.</summary>
public interface IBackButtonController : IDisposable
{
    IReadOnlyList<BackButtonKey> SupportedKeys { get; }
    Task<BackButtonBinding?> ReadAsync(BackButtonDefinition button);
    Task<bool> WriteAsync(BackButtonDefinition button, BackButtonBinding binding);
    Task<bool> EnsureActiveAsync();
    Task<bool> SaveAsync();
}

public enum BackButtonOpenFailure
{
    None,
    NotFound,
    FirmwareTooOld,
    NotAnswering,
}

public readonly record struct BackButtonOpenResult(
    IBackButtonController? Controller,
    BackButtonOpenFailure Failure = BackButtonOpenFailure.None,
    string? FirmwareVersion = null);

public static class BackButtonControllers
{
    public static BackButtonOpenResult Open(BackButtonsProfile profile) => profile.Protocol switch
    {
        BackButtonsProfile.MsiHidV1 => MsiBackButtonController.Open(profile),
        _ => new(null, BackButtonOpenFailure.NotFound),
    };

    public static IReadOnlyList<BackButtonKey> KeysFor(BackButtonsProfile? profile) => profile?.Protocol switch
    {
        BackButtonsProfile.MsiHidV1 => BackButtonKey.All,
        _ => [BackButtonKey.Unbound],
    };
}

/// <summary>MSI Claw vendor HID back-button controller (<c>msi-hid-v1</c>).</summary>
internal sealed class MsiBackButtonController : IBackButtonController
{
    private static readonly TimeSpan SwitchSettle = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SwitchGiveUp = TimeSpan.FromSeconds(2);

    private readonly BackButtonsProfile _profile;
    private ClawController? _controller;

    private MsiBackButtonController(BackButtonsProfile profile, ClawController controller)
    {
        _profile = profile;
        _controller = controller;
    }

    public IReadOnlyList<BackButtonKey> SupportedKeys => BackButtonKey.All;

    public static BackButtonOpenResult Open(BackButtonsProfile profile)
    {
        var controller = ClawController.Find(profile.VendorId, profile.ProductIds);
        if (controller is null)
            return new(null, BackButtonOpenFailure.NotFound);

        if (controller.IsUnsupportedProfileFirmware(profile.MinFirmwareVersion, boardKnown: true))
        {
            var raw = controller.FirmwareVersionRaw;
            var version = controller.FirmwareVersion;
            controller.Dispose();
            return raw == 0
                ? new(null, BackButtonOpenFailure.NotAnswering)
                : new(null, BackButtonOpenFailure.FirmwareTooOld, version);
        }

        return new(new MsiBackButtonController(profile, controller));
    }

    public async Task<BackButtonBinding?> ReadAsync(BackButtonDefinition button)
    {
        if (_controller is null) return null;

        var raw = await _controller.ReadProfileAsync(button.Offset, _profile.BlockLength, _profile.ProfileLength);
        return raw is null ? null : BackButtonBinding.Decode(raw, _profile.MaxSimultaneousKeys);
    }

    public Task<bool> WriteAsync(BackButtonDefinition button, BackButtonBinding binding)
    {
        if (_controller is null) return Task.FromResult(false);

        var encoded = binding.Encode(_profile.BlockLength, _profile.MaxSimultaneousKeys);
        return _controller.WriteBackButtonAsync(_profile, button, encoded);
    }

    /// <summary>Sets M-key mode to 0 (custom remaps) and waits for read-back, re-opening if USB re-enumerates.</summary>
    public async Task<bool> EnsureActiveAsync()
    {
        if (_controller is null) return false;

        var current = await _controller.ReadModeAsync();
        if (current is not { } now) return false;
        if (now.MKeys == 0) return true;

        var padMode = now.Mode == (byte)PadMode.Cursor ? PadMode.Cursor : PadMode.Gamepad;
        if (!await _controller.SwitchModeAsync(padMode, mKeys: 0)) return false;

        var started = DateTime.UtcNow;
        await Task.Delay(SwitchSettle);

        while (DateTime.UtcNow - started < SwitchGiveUp)
        {
            if (_controller is not null)
            {
                var verified = await _controller.ReadModeAsync();
                if (verified is { MKeys: 0 }) return true;
            }

            _controller?.Dispose();
            _controller = null;
            await Task.Delay(ReopenDelay);
            _controller = await Task.Run(() => ClawController.Find(_profile.VendorId, _profile.ProductIds));
        }

        return false;
    }

    public Task<bool> SaveAsync() => _controller?.SaveAsync() ?? Task.FromResult(false);

    public void Dispose()
    {
        _controller?.Dispose();
        _controller = null;
    }
}
