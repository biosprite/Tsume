using System.Management;
using Tsume.Core.Diagnostics;

namespace Tsume.App.Input;

/// <summary>The Claw's two dedicated buttons, as the firmware reports them.</summary>
public enum ClawButton
{
    /// <summary>The "M" button, short press.</summary>
    MsiCenter = 0x29,

    /// <summary>The "M" button, held.</summary>
    MsiCenterHeld = 0x31,

    /// <summary>The Quick Settings button, short press.</summary>
    QuickSettings = 0x58,

    /// <summary>The Quick Settings button, held.</summary>
    QuickSettingsHeld = 0x2A,
}

/// <summary>
/// Listens for the Claw's dedicated buttons. They are not part of the gamepad: the firmware
/// raises them as the WMI event <c>MSI_Event</c>, which needs no elevation to subscribe to.
/// </summary>
public sealed class ClawButtons : IDisposable
{
    private readonly ManagementEventWatcher _watcher = new(@"root\WMI", "SELECT * FROM MSI_Event");

    /// <summary>Every event code is recorded, so unknown buttons can be identified later.</summary>
    private readonly Journal _journal = new("buttons");

    /// <summary>Raised on a background thread. Only presses are reported — never releases.</summary>
    public event Action<ClawButton>? Pressed;

    public ClawButtons()
    {
        _watcher.EventArrived += OnEvent;

        // Subscribing can take a moment; do not hold up start-up for it.
        Task.Run(() =>
        {
            try { _watcher.Start(); }
            catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _journal.Write($"Could not listen for buttons: {error.Message}");
            }
        });
    }

    private void OnEvent(object sender, EventArrivedEventArgs e)
    {
        var raw = Convert.ToInt32(e.NewEvent["MSIEvt"]);
        var code = raw & 0xFF;

        var known = Enum.IsDefined(typeof(ClawButton), code);

        // Act before logging: a press should not wait on the disk.
        if (known) Pressed?.Invoke((ClawButton)code);

        _journal.Write($"event 0x{raw:X4}  code 0x{code:X2}  {(known ? ((ClawButton)code).ToString() : "unknown")}");
    }

    public void Dispose()
    {
        try { _watcher.Stop(); }
        catch (Exception error) when (error is ManagementException or System.Runtime.InteropServices.COMException) { }

        _watcher.Dispose();
    }
}
