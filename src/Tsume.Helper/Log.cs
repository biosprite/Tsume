using Tsume.Core.Diagnostics;

namespace Tsume.Helper;

/// <summary>The helper's log: %LOCALAPPDATA%\Tsume\helper.log.</summary>
internal static class Log
{
    private static readonly Journal Journal = new("helper");

    public static void Write(string message) => Journal.Write(message);

    public static string? Read() => Journal.Read();
}
