using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SkyScope.Core;

public record UpdateCheckResult(bool UpdateAvailable, string? LatestVersion);

public static class GitHubUpdateChecker
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(string owner, string repo, string currentVersion)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            // GitHub's API rejects requests with no User-Agent.
            request.Headers.UserAgent.ParseAdd("SkyScope-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return new UpdateCheckResult(false, null);

            var release = await response.Content.ReadFromJsonAsync<GitHubReleaseResponse>();
            var tag = release?.TagName;
            if (string.IsNullOrWhiteSpace(tag)) return new UpdateCheckResult(false, null);

            // Versions are Major.Minor.yyMM.CommitCount (4 segments) since the switch to
            // Directory.Build.props auto-versioning. Older releases used a 2-segment yyyyMM.N tag —
            // comparing across the two schemes numerically is meaningless (e.g. 202608 > 1), so only
            // compare when both sides are already on the new 4-segment scheme.
            var updateAvailable =
                currentVersion.Split('.').Length == 4 && tag.Split('.').Length == 4 &&
                Version.TryParse(currentVersion, out var current) &&
                Version.TryParse(tag, out var latest) &&
                latest > current;

            return new UpdateCheckResult(updateAvailable, tag);
        }
        catch
        {
            // Best-effort — no network, rate-limited, unparseable response, etc. Just skip the check.
            return new UpdateCheckResult(false, null);
        }
    }

    private class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}
