namespace Tsume.Upscaling.Install;

/// <summary>
/// The system DLL names OptiScaler can be loaded under (it poses as a file the game loads anyway),
/// in the order to try them.
/// </summary>
public static class ProxyNames
{
    /// <summary>
    /// dxgi.dll suits nearly every DirectX game. Vulkan games never load it and need winmm.dll;
    /// Xbox-app games often do better with winmm.dll or version.dll.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
        ["dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];

    public static bool IsOne(string name) => All.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name to install under: the existing OptiScaler's name if there is one (two copies under two names
    /// would both load and the game would not start), else the one asked for, else the first name not already taken (ReShade, DXVK …).
    /// </summary>
    public static string Choose(string folder, string asked, string? existing)
    {
        if (existing is not null) return existing;
        if (IsOne(asked)) return All.First(name => name.Equals(asked, StringComparison.OrdinalIgnoreCase));

        return All.FirstOrDefault(name => !File.Exists(Path.Combine(folder, name))) ?? All[0];
    }
}
