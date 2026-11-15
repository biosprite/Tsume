using System.Net.Http;
using System.Net.Http.Headers;
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

    internal static readonly HttpClient Web = NewClient();

    public static async Task<NightlyBuild> NewestAsync(CancellationToken cancel = default)
    {
        string list;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeSpan.FromSeconds(20));

            list = await Web.GetStringAsync(Releases, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            throw new UpscalingException(Trouble.Offline, error.Message, error);
        }

        try
        {
            using var releases = JsonDocument.Parse(list);

            // Newest first. Take the first one that carries a build Tsume can check.
            foreach (var release in releases.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;

                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                    var digest = asset.TryGetProperty("digest", out var given) ? given.GetString() : null;

                    var isBuild = name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)
                               && name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                               && url.StartsWith(Downloads, StringComparison.Ordinal);

                    // No published hash, no download: there would be nothing to check it against.
                    if (!isBuild || digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) continue;

                    return new NightlyBuild(
                        release.GetProperty("tag_name").GetString()!,
                        name, url,
                        asset.GetProperty("size").GetInt64(),
                        digest["sha256:".Length..].ToLowerInvariant());
                }
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpscalingException(Trouble.NoBuildPublished, error.Message, error);
        }

        throw new UpscalingException(Trouble.NoBuildPublished);
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // each request sets its own limit

        // GitHub turns away requests that do not say what is asking.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tsume", "1"));

        return client;
    }
}
