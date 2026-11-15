namespace Tsume.Core.Controller;

// Deliberately only two: the protocol also has DInput, MSI's own, and two service modes that
// must never be sent. Leaving them out of this type keeps them from being sent.
/// <summary>
/// What the Claw's built-in pad presents itself as. Values are the vendor protocol's mode codes
/// (see <c>docs/hardware/controller.md</c>).
/// </summary>
public enum PadMode : byte
{
    /// <summary>An Xbox controller (XInput). What games expect.</summary>
    Gamepad = 1,

    /// <summary>A mouse and keyboard: the stick moves the cursor. For the Windows desktop.</summary>
    Cursor = 4,
}
