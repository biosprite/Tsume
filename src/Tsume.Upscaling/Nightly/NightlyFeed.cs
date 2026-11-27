using System.Text.Json;

namespace Tsume.Upscaling.Nightly;

/// <summary>
/// Asks GitHub for the newest nightly build of OptiScaler. Only the nightly channel is read:
/// FSR 4's INT8 networks on Intel Arc are not in the numbered releases.
/// </summary>
public static class NightlyFeed
{
    private const string Releases = "https://api.github.com/repos/optiscaler/OptiScaler-nightly/releases?per_page=5";

    /// <summary>The only URL prefix a download is accepted from, whatever the release list says.</summary>
    private const string Downloads = "https://github.com/optiscaler/OptiScaler-nightly/releases/download/";

    public static async Task<NightlyBuild> NewestAsync(CancellationToken cancel = default)
    {
        var list = await RemoteStore.GetStringAsync(Releases, cancel).ConfigureAwait(false);

        try
        {
            using var releases = JsonDocument.Parse(list);

            // Newest first. Take the first one that carries a build Tsume can check.
            foreach (var release in releases.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    if (!RemoteStore.TryReadVerifiedAsset(
                            asset,
                            Downloads,
                            name => name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)
                                 && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase),
                            out var name, out var url, out var size, out var sha256))
                    {
                        continue;
                    }

                    return new NightlyBuild(
                        release.GetProperty("tag_name").GetString()!,
                        name, url, size, sha256);
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpscalingException(Trouble.NoBuildPublished, error.Message, error);
        }

        throw new UpscalingException(Trouble.NoBuildPublished);
    }
}
