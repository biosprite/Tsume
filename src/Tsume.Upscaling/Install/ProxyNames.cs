namespace Tsume.Upscaling.Install;

/// <summary>
/// The system DLL names OptiScaler can be loaded under (it poses as a file the game loads anyway),
/// with <c>dxgi.dll</c> first as the default.
/// </summary>
public static class ProxyNames
{
    public const string Default = "dxgi.dll";
    public const string AsiName = "OptiScaler.asi";

    /// <summary>
    /// Selectable proxy DLL names in UI order. <c>dxgi.dll</c> suits most DirectX games;
    /// Vulkan games never load it and need <c>winmm.dll</c>; Xbox Game Pass games often need <c>winmm.dll</c> or <c>version.dll</c>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        [Default, "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];

    /// <summary>Every file name OptiScaler may already be installed under (including manual ASI installs).</summary>
    public static IReadOnlyList<string> Recognised { get; } = [.. All, AsiName];

    public static bool IsOne(string? name) =>
        !string.IsNullOrEmpty(name) && All.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical casing in <see cref="All"/> (or <see cref="AsiName"/>), defaulting to <see cref="Default"/>.</summary>
    public static string Normalize(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            if (All.FirstOrDefault(known => known.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match;

            if (name.Equals(AsiName, StringComparison.OrdinalIgnoreCase))
                return AsiName;
        }

        return Default;
    }

    /// <summary>
    /// The name to install under: an existing OptiScaler's name if one is already in the folder (two copies under two
    /// names would both load and crash the game), otherwise the exact proxy DLL selected by the user.
    /// </summary>
    public static string Choose(string asked, string? existing) =>
        existing is not null ? Normalize(existing) : IsOne(asked) ? Normalize(asked) : Default;
}
