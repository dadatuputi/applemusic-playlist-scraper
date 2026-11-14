using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AppleMusicPlaylist;

/// <summary>Result of parsing one playlist HTML page (holds at most 300 tracks).</summary>
public sealed record PlaylistPage(string? Name, string? Curator, IReadOnlyList<PlaylistTrack> Tracks, string? NextPath);

/// <summary>Result of parsing one page of the catalog tracks API.</summary>
public sealed record TracksApiPage(IReadOnlyList<PlaylistTrack> Tracks, string? NextPath, int SkippedItems);

/// <summary>
/// Pure parsing functions. No network access, so the unit tests run them on saved fixtures.
/// </summary>
public static partial class PageParser
{
    [GeneratedRegex(@"<script[^>]*\bid=""serialized-server-data""[^>]*>(?<json>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ServerDataRegex();

    [GeneratedRegex(@"<meta\s+(?:name|property)=""(?<k>[^""]+)""\s+content=""(?<v>[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"<title>(?<t>.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"[""'](?<p>/assets/index[^""']*\.js)[""']")]
    private static partial Regex BundleRegex();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]+\.eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+")]
    private static partial Regex JwtRegex();

    /// <summary>Returns the JSON text inside <c>&lt;script id="serialized-server-data"&gt;</c>, or null.</summary>
    public static string? ExtractServerDataJson(string html)
    {
        var m = ServerDataRegex().Match(html);
        return m.Success ? m.Groups["json"].Value.Trim() : null;
    }

    /// <summary>Parses the playlist HTML page.</summary>
    /// <exception cref="AppleMusicScrapeException">The page has no server data JSON.</exception>
    public static PlaylistPage ParsePage(string html)
    {
        var json = ExtractServerDataJson(html)
            ?? throw new AppleMusicScrapeException("Page has no serialized-server-data script; Apple may have changed the page.");
        var page = ParseServerData(json);
        var meta = ReadMeta(html);
        var name = page.Name
                   ?? meta.GetValueOrDefault("apple:title")
                   ?? CleanTitle(meta.GetValueOrDefault("og:title"))
                   ?? CleanTitle(TitleRegex().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups["t"].Value) : null);
        return page with { Name = name };
    }

    /// <summary>Parses the serialized-server-data JSON on its own (what the fixtures hold).</summary>
    public static PlaylistPage ParseServerData(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e)
        {
            try { doc = JsonDocument.Parse(WebUtility.HtmlDecode(json)); }
            catch (JsonException) { throw new AppleMusicScrapeException("serialized-server-data is not valid JSON.", e); }
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Prefer the track-list sections; fall back to the whole document.
            var trackSections = new List<JsonElement>();
            string? name = null, curator = null;
            foreach (var section in FindAll(root, e => e.ValueKind == JsonValueKind.Object
                                                   && e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                                                   && e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array))
            {
                var id = section.GetProperty("id").GetString() ?? "";
                if (id.StartsWith("track-list", StringComparison.Ordinal))
                    trackSections.Add(section);
                else if (name is null && id.Contains("header", StringComparison.OrdinalIgnoreCase))
                    (name, curator) = ReadHeader(section);
            }

            var tracks = new List<PlaylistTrack>();
            IEnumerable<JsonElement> scopes = trackSections.Count > 0 ? trackSections : new[] { root };
            foreach (var scope in scopes)
                foreach (var song in FindAll(scope, IsSongLockup))
                    if (ToTrack(song) is { } t) tracks.Add(t);

            var next = FindAll(root, e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("nextIntent", out _))
                .Select(e => e.GetProperty("nextIntent"))
                .Where(n => n.ValueKind == JsonValueKind.Object && n.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                .Select(n => n.GetProperty("url").GetString())
                .FirstOrDefault(u => !string.IsNullOrEmpty(u));

            return new PlaylistPage(name, curator, tracks, next);
        }
    }

    /// <summary>Parses one response of <c>/v1/catalog/{sf}/playlists/{id}/tracks</c>.</summary>
    public static TracksApiPage ParseTracksApi(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new AppleMusicScrapeException("Tracks API response has no data array.");

        var tracks = new List<PlaylistTrack>();
        var skipped = 0;
        foreach (var item in data.EnumerateArray())
        {
            var type = Str(item, "type");
            if (type is not ("songs" or "library-songs") || !item.TryGetProperty("attributes", out var a))
            {
                skipped++;
                continue;
            }
            var title = Str(a, "name");
            var artist = Str(a, "artistName");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) { skipped++; continue; }
            tracks.Add(new PlaylistTrack(title, artist, NullIfEmpty(Str(a, "albumName")), NullIfEmpty(Str(a, "albumArtistName"))));
        }
        var next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new TracksApiPage(tracks, next, skipped);
    }

    /// <summary>Finds the path of the main JS bundle (e.g. <c>/assets/index~abc123.js</c>).</summary>
    public static string? FindBundlePath(string html)
    {
        var m = BundleRegex().Match(html);
        return m.Success ? m.Groups["p"].Value : null;
    }

    /// <summary>
    /// Finds Apple's web-player token in the JS bundle: a JWT whose header <c>kid</c> is
    /// <c>WebPlayKid</c> (or whose payload <c>iss</c> is <c>AMPWebPlay</c>) and that is not expired.
    /// </summary>
    public static string? FindWebPlayToken(string bundleJs, DateTimeOffset now)
    {
        string? fallback = null;
        foreach (Match m in JwtRegex().Matches(bundleJs))
        {
            var parts = m.Value.Split('.');
            using var header = TryDecodeJson(parts[0]);
            using var payload = TryDecodeJson(parts[1]);
            if (header is null || payload is null) continue;

            if (payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var e)
                && DateTimeOffset.FromUnixTimeSeconds(e) <= now)
                continue;

            if (Str(header.RootElement, "kid") == "WebPlayKid") return m.Value;
            if (Str(payload.RootElement, "iss") == "AMPWebPlay") fallback ??= m.Value;
        }
        return fallback;
    }

    // ---- helpers -------------------------------------------------------------------------

    private static bool IsSongLockup(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty("contentDescriptor", out var cd) && cd.ValueKind == JsonValueKind.Object
        && Str(cd, "kind") == "song"
        && e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String;

    private static PlaylistTrack? ToTrack(JsonElement e)
    {
        var title = Str(e, "title");
        var artist = Str(e, "artistName") ?? FirstLinkTitle(e, "subtitleLinks");
        var album = FirstLinkTitle(e, "tertiaryLinks");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) return null;
        return new PlaylistTrack(title, artist, NullIfEmpty(album));
    }

    private static (string?, string?) ReadHeader(JsonElement section)
    {
        foreach (var item in section.GetProperty("items").EnumerateArray())
        {
            var title = Str(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;
            var curator = FirstLinkTitle(item, "subtitleLinks") ?? Str(item, "subtitle");
            return (title, NullIfEmpty(curator));
        }
        return (null, null);
    }

    private static string? FirstLinkTitle(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var links) && links.ValueKind == JsonValueKind.Array && links.GetArrayLength() > 0
            ? Str(links[0], "title")
            : null;

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Depth-first walk returning every element that matches, without descending into matches.</summary>
    private static IEnumerable<JsonElement> FindAll(JsonElement root, Func<JsonElement, bool> match)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (match(e)) { yield return e; continue; }
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject().Reverse()) stack.Push(p.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var c in e.EnumerateArray().Reverse()) stack.Push(c);
                    break;
            }
        }
    }

    private static Dictionary<string, string> ReadMeta(string html)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in MetaRegex().Matches(html))
            d.TryAdd(m.Groups["k"].Value, WebUtility.HtmlDecode(m.Groups["v"].Value));
        return d;
    }

    private static string? CleanTitle(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = t.Trim().TrimStart('‎');
        // "Today's Hits - Playlist - Apple Music" -> "Today's Hits"
        foreach (var suffix in new[] { " - Apple Music", " - Playlist" })
            if (t.EndsWith(suffix, StringComparison.Ordinal)) t = t[..^suffix.Length];
        return t.Trim();
    }

    private static JsonDocument? TryDecodeJson(string b64url)
    {
        try
        {
            var s = b64url.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s)));
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
