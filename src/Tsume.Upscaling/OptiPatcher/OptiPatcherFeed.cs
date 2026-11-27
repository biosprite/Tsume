using System.Text.Json;

namespace Tsume.Upscaling.OptiPatcher;

/// <summary>
/// Asks GitHub for the newest numbered release of OptiPatcher (e.g. v0.42).
/// </summary>
public static class OptiPatcherFeed
{
    private const string Releases = "https://api.github.com/repos/optiscaler/OptiPatcher/releases?per_page=10";

    /// <summary>The only URL prefix a download is accepted from, whatever the release list says.</summary>
    private const string Downloads = "https://github.com/optiscaler/OptiPatcher/releases/download/";

    public static async Task<OptiPatcherRelease> NewestAsync(CancellationToken cancel = default)
    {
        var list = await RemoteStore.GetStringAsync(Releases, cancel).ConfigureAwait(false);

        try
        {
            using var releases = JsonDocument.Parse(list);

            // Prefer the newest numbered stable release (v0.42, etc.); ignore drafts, prereleases, and the unversioned rolling tag.
            var found = Pick(releases.RootElement, numberedOnly: true)
                     ?? Pick(releases.RootElement, numberedOnly: false);

            if (found is not null) return found;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpscalingException(Trouble.NoOptiPatcherPublished, error.Message, error);
        }

        throw new UpscalingException(Trouble.NoOptiPatcherPublished);
    }

    private static OptiPatcherRelease? Pick(JsonElement releases, bool numberedOnly)
    {
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) continue;

            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(tag)) continue;

            if (numberedOnly)
            {
                if (tag.Equals("rolling", StringComparison.OrdinalIgnoreCase)) continue;
                if (!tag.StartsWith('v') && !tag.StartsWith('V')) continue;
            }

            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (!RemoteStore.TryReadVerifiedAsset(
                        asset,
                        Downloads,
                        name => name.StartsWith("OptiPatcher", StringComparison.OrdinalIgnoreCase)
                             && name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase),
                        out var name, out var url, out var size, out var sha256))
                {
                    continue;
                }

                return new OptiPatcherRelease(tag, name, url, size, sha256);
            }
        }

        return null;
    }
}
