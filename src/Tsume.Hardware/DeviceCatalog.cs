using Tsume.Hardware.Acer;
using Tsume.Hardware.Msi;

namespace Tsume.Hardware;

/// <summary>
/// Every kind of machine Tsume can drive, one entry per maker. A maker returns a device only for a board
/// verified against the real machine; otherwise there is no device and nothing is written.
/// </summary>
public static class DeviceCatalog
{
    private static readonly Func<Board, IDevice?>[] Makers =
    [
        MsiDevice.TryOpen,
        AcerDevice.TryOpen,
    ];

    /// <summary>True when <paramref name="board"/> matches a board Tsume supports, even if its firmware endpoints are not ready yet.</summary>
    public static bool IsKnownBoard(Board board) =>
        MsiBoards.Find(board) is not null || AcerBoards.Find(board) is not null;

    /// <returns>The board found, and the device that drives it — null on a machine Tsume does not know.</returns>
    public static (Board Board, IDevice? Device) Open()
    {
        var board = Board.Detect();

        foreach (var maker in Makers)
            if (maker(board) is { } device)
                return (board, device);

        return (board, null);
    }
}
