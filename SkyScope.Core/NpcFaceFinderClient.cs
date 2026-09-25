using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SkyScope.Core;

public class NpcFaceFinderFace
{
    public string ImageUrl { get; init; } = "";
    public int    ModId    { get; init; }
    public string ModName  { get; init; } = "";
}

// One entry from the site's full mod catalog (/api/mods) — cached on disk so the Settings tab can
// offer a searchable manual picker without re-fetching hundreds of mods on every open.
public class NpcFaceFinderMod
{
    public int     Id          { get; init; }
    public string  Name        { get; init; } = "";
    public string? ExternalId  { get; init; } // Nexus mod id, when external_source is "nexusmods"
    public string? AuthorName  { get; init; }
    public string? ExternalUrl { get; init; } // the mod's own page (e.g. Nexus URL), for "open in browser"

    public string DisplayText => string.IsNullOrEmpty(AuthorName) ? Name : $"{Name} ({AuthorName})";
    public bool   HasExternalUrl => !string.IsNullOrEmpty(ExternalUrl);
}

// Best-effort client for npcfacefinder.com's public API — mirrors GitHubUpdateChecker's pattern
// (shared HttpClient, System.Text.Json, swallow all failures into an empty/null result). The
// search endpoint 500s unless sort/direction are both present; a CookieContainer carries the
// csrf-token cookie set by /api/csrf-token across later requests.
public static class NpcFaceFinderClient
{
    private static readonly CookieContainer Cookies = new();
    private static readonly HttpClient Client = new(new HttpClientHandler { CookieContainer = Cookies })
    {
        BaseAddress = new Uri("https://npcfacefinder.com"),
        Timeout     = TimeSpan.FromSeconds(10)
    };

    // A community-run site, not worth hammering — cap concurrent outbound requests.
    private static readonly SemaphoreSlim Gate = new(2);
    private static bool _csrfReady;

    // Finds the npcfacefinder NPC id matching an exact (plugin, formId) pair, searching by name
    // first (the only lookup the API offers) then filtering candidates by source+ref_id, which
    // uniquely identifies the record — the name search is just how you get there.
    public static async Task<int?> FindNpcIdAsync(string name, string plugin, string formId)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(plugin) || string.IsNullOrEmpty(formId))
            return null;

        try
        {
            if (!await EnsureCsrfTokenAsync()) return null;

            await Gate.WaitAsync();
            SearchResponse? result;
            try
            {
                result = await Client.GetFromJsonAsync<SearchResponse>(
                    $"/api/npc/search?search={Uri.EscapeDataString(name)}&page=0&sort=npc_name&direction=asc");
            }
            finally { Gate.Release(); }

            return result?.Npcs.FirstOrDefault(n =>
                string.Equals(n.Source, plugin, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(n.RefId, formId, StringComparison.OrdinalIgnoreCase))?.Id;
        }
        catch
        {
            return null; // network down, site error, unparseable response — just skip it
        }
    }

    // Paginated, same as GetAllModsAsync — a popular NPC (e.g. a common replacer target) can have
    // more faces than fit on one page, and "total" is only present on page 0's response, so a
    // manually-matched mod's face can silently sit on a page this never used to request.
    public static async Task<List<NpcFaceFinderFace>> GetFacesAsync(int npcId)
    {
        try
        {
            if (!await EnsureCsrfTokenAsync()) return [];

            var faces = new List<NpcFaceFinderFace>();
            var page = 0;
            var expectedTotal = int.MaxValue;

            while (true)
            {
                await Gate.WaitAsync();
                FacesResponse? result;
                try
                {
                    result = await Client.GetFromJsonAsync<FacesResponse>(
                        $"/api/npc/{npcId}/faces?page={page}&sort=face_created_at&direction=desc");
                }
                finally { Gate.Release(); }

                if (result == null || result.Faces.Count == 0) break;

                if (page == 0 && result.Total > 0) expectedTotal = result.Total;

                faces.AddRange(result.Faces
                    .Where(f => !string.IsNullOrEmpty(f.ImageUrl) && f.Mod != null)
                    .Select(f => new NpcFaceFinderFace { ImageUrl = RewriteImageUrl(f.ImageUrl), ModId = f.Mod!.Id, ModName = f.Mod!.Name }));

                if (faces.Count >= expectedTotal) break;
                page++;
            }

            return faces;
        }
        catch
        {
            return [];
        }
    }

    // Fetches the site's full mod catalog (~800+ entries, ~25/page) — always all pages, since the
    // Settings picker needs the whole list to search/filter locally. Caller decides how often to
    // call this (e.g. once per day, cached to disk) rather than on every use.
    public static async Task<List<NpcFaceFinderMod>> GetAllModsAsync()
    {
        try
        {
            if (!await EnsureCsrfTokenAsync()) return [];

            var mods = new List<NpcFaceFinderMod>();
            var page = 0;
            // "total" is only present on page 0's response — later pages omit it (defaulting the
            // DTO's int to 0), so it's captured once here rather than re-read from every page.
            var expectedTotal = int.MaxValue;

            while (true)
            {
                await Gate.WaitAsync();
                ModsResponse? result;
                try
                {
                    result = await Client.GetFromJsonAsync<ModsResponse>(
                        $"/api/mods?page={page}&sort=mod_created_at&direction=desc");
                }
                finally { Gate.Release(); }

                if (result == null || result.Mods.Count == 0) break;

                if (page == 0 && result.Total > 0) expectedTotal = result.Total;

                mods.AddRange(result.Mods.Select(m => new NpcFaceFinderMod
                {
                    Id          = m.Id,
                    Name        = m.Name,
                    ExternalId  = m.ExternalSource == "nexusmods" ? m.ExternalId : null,
                    AuthorName  = m.Author?.Name,
                    ExternalUrl = m.ExternalUrl
                }));

                if (mods.Count >= expectedTotal) break;
                page++;
            }

            return mods;
        }
        catch
        {
            return [];
        }
    }

    // Disk-cached wrapper around GetAllModsAsync — refetches only when the cache is missing, empty,
    // or older than maxAge, so opening the Settings picker doesn't re-fetch ~800 mods every time.
    // Pass TimeSpan.Zero to force a refresh.
    public static async Task<List<NpcFaceFinderMod>> GetCachedModsAsync(string cacheFilePath, TimeSpan maxAge)
    {
        try
        {
            if (File.Exists(cacheFilePath))
            {
                var json   = await File.ReadAllTextAsync(cacheFilePath);
                var cached = JsonSerializer.Deserialize<ModsCacheFile>(json);
                if (cached != null && cached.Mods.Count > 0 && DateTime.UtcNow - cached.FetchedAtUtc < maxAge)
                    return cached.Mods;
            }
        }
        catch { /* fall through and refetch */ }

        var mods = await GetAllModsAsync();
        if (mods.Count > 0)
        {
            try
            {
                var dir = Path.GetDirectoryName(cacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var payload = new ModsCacheFile { FetchedAtUtc = DateTime.UtcNow, Mods = mods };
                await File.WriteAllTextAsync(cacheFilePath, JsonSerializer.Serialize(payload));
            }
            catch { /* best-effort — an unwritable cache just means refetching next time */ }
        }

        return mods;
    }

    // Returns the failure reason rather than swallowing it — the caller surfaces it in the
    // portrait placeholder's tooltip, so a download failure isn't indistinguishable from any
    // other reason no portrait showed up.
    public static async Task<(bool Success, string? Error)> DownloadImageAsync(string imageUrl, string destinationPath)
    {
        try
        {
            await Gate.WaitAsync();
            byte[] bytes;
            try
            {
                bytes = await Client.GetByteArrayAsync(imageUrl);
            }
            finally { Gate.Release(); }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(destinationPath, bytes);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // The API returns a direct Supabase storage URL, but that bucket isn't publicly reachable —
    // only npcfacefinder's own /img/ reverse proxy can actually fetch it (confirmed: the raw
    // Supabase URL 400s "Bucket not found" for every image tested, while the same path through
    // npcfacefinder.com/img/ returns 200). Rewriting to the same path under that proxy instead.
    private static string RewriteImageUrl(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return rawUrl;
        try
        {
            var path = new Uri(rawUrl).PathAndQuery;
            return $"https://npcfacefinder.com/img{path}";
        }
        catch (UriFormatException)
        {
            return rawUrl; // malformed — leave as-is, the download will just fail cleanly later
        }
    }

    private static async Task<bool> EnsureCsrfTokenAsync()
    {
        if (_csrfReady) return true;

        await Gate.WaitAsync();
        try
        {
            if (_csrfReady) return true;
            using var response = await Client.GetAsync("/api/csrf-token");
            _csrfReady = response.IsSuccessStatusCode;
            return _csrfReady;
        }
        catch
        {
            return false;
        }
        finally { Gate.Release(); }
    }

    private class SearchResponse
    {
        [JsonPropertyName("npcs")]
        public List<NpcSearchEntry> Npcs { get; set; } = [];
    }

    private class NpcSearchEntry
    {
        [JsonPropertyName("id")]     public int    Id     { get; set; }
        [JsonPropertyName("ref_id")] public string RefId  { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
    }

    private class FacesResponse
    {
        [JsonPropertyName("faces")]
        public List<FaceEntry> Faces { get; set; } = [];

        [JsonPropertyName("total")]
        public int Total { get; set; }
    }

    private class FaceEntry
    {
        [JsonPropertyName("image_url")] public string   ImageUrl { get; set; } = "";
        [JsonPropertyName("mod")]        public ModEntry? Mod     { get; set; }
    }

    private class ModEntry
    {
        [JsonPropertyName("id")]   public int    Id   { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private class ModsResponse
    {
        [JsonPropertyName("mods")]  public List<ModListEntry> Mods  { get; set; } = [];
        [JsonPropertyName("total")] public int                Total { get; set; }
    }

    private class ModListEntry
    {
        [JsonPropertyName("id")]              public int         Id             { get; set; }
        [JsonPropertyName("name")]            public string      Name           { get; set; } = "";
        [JsonPropertyName("external_id")]     public string?     ExternalId     { get; set; }
        [JsonPropertyName("external_source")] public string?     ExternalSource { get; set; }
        [JsonPropertyName("external_url")]    public string?     ExternalUrl    { get; set; }
        [JsonPropertyName("author")]          public AuthorEntry? Author        { get; set; }
    }

    private class AuthorEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private class ModsCacheFile
    {
        public DateTime FetchedAtUtc { get; set; }
        public List<NpcFaceFinderMod> Mods { get; set; } = [];
    }
}
