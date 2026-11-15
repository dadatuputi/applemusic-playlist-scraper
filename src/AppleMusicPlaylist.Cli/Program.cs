using System.Text.Encodings.Web;
using System.Text.Json;
using AppleMusicPlaylist;

// applemusic-playlist <playlist-url> [options]
//   --format text|json|albums   output (default text)
//   --page-only                 do not page past the first 300 tracks
//   --min-tracks N              exit 2 if fewer tracks were read (canary)
//   --expect-complete           exit 2 if the result is truncated (canary)
//   --dump DIR                  save the page's server-data JSON and API pages (fixture capture)

var argList = args.ToList();

// Canary for the library path: an invalid user token must give AppleMusicAuthException,
// which proves the web-player token was found and the library endpoint still answers 401/403.
if (argList.Remove("--library-auth-check"))
{
    using var c = new AppleMusicPlaylistClient();
    try
    {
        await c.GetLibraryPlaylistsAsync("invalid-user-token");
        Console.Error.WriteLine("FAIL: an invalid user token was accepted");
        return 2;
    }
    catch (AppleMusicAuthException e)
    {
        Console.Error.WriteLine($"OK: {e.Message}");
        return 0;
    }
    catch (Exception e) when (e is HttpRequestException or AppleMusicScrapeException)
    {
        Console.Error.WriteLine($"FAIL: {e.Message}");
        return 2;
    }
}
if (argList.Count == 0 || argList.Contains("-h") || argList.Contains("--help"))
{
    Console.Error.WriteLine("usage: applemusic-playlist <url> [--format text|json|albums] [--page-only] [--min-tracks N] [--expect-complete] [--dump DIR]");
    return 64;
}

string? Opt(string name)
{
    var i = argList.IndexOf(name);
    if (i < 0 || i + 1 >= argList.Count) return null;
    var v = argList[i + 1];
    argList.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => argList.Remove(name);

var format = Opt("--format") ?? "text";
var minTracks = int.TryParse(Opt("--min-tracks"), out var mt) ? mt : 0;
var dumpDir = Opt("--dump");
var pageOnly = Flag("--page-only");
var expectComplete = Flag("--expect-complete");
if (argList.Count != 1) { Console.Error.WriteLine("Expected exactly one playlist URL."); return 64; }

HttpMessageHandler handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
if (dumpDir is not null) handler = new DumpHandler(dumpDir, handler);
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
using var client = new AppleMusicPlaylistClient(http, new AppleMusicPlaylistOptions { FetchAllPages = !pageOnly });

Playlist pl;
try
{
    pl = await client.GetPlaylistAsync(argList[0]);
}
catch (Exception e) when (e is HttpRequestException or AppleMusicScrapeException or ArgumentException)
{
    Console.Error.WriteLine($"ERROR: {e.Message}");
    return 1;
}

var jsonOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
switch (format)
{
    case "json":
        Console.WriteLine(JsonSerializer.Serialize(pl, jsonOpts));
        break;
    case "albums":
        foreach (var g in pl.Tracks.Where(t => t.AlbumName is not null)
                     .GroupBy(t => (t.ArtistName, t.AlbumName)).OrderBy(g => g.Key.ArtistName).ThenBy(g => g.Key.AlbumName))
            Console.WriteLine($"{g.Key.ArtistName}\t{g.Key.AlbumName}\t{g.Count()}");
        break;
    default:
        foreach (var t in pl.Tracks) Console.WriteLine($"{t.ArtistName}\t{t.AlbumName}\t{t.Title}");
        break;
}

var albums = pl.Tracks.Where(t => t.AlbumName is not null).Select(t => (t.ArtistName, t.AlbumName)).Distinct().Count();
var withAlbum = pl.Tracks.Count == 0 ? 0 : pl.Tracks.Count(t => t.AlbumName is not null) * 100 / pl.Tracks.Count;
Console.Error.WriteLine($"\"{pl.Name}\" ({pl.Id}, {pl.Storefront}) by {pl.Curator ?? "?"}: {pl.Tracks.Count} tracks, {albums} albums, {withAlbum}% with album, truncated={pl.Truncated}{(pl.TruncationReason is null ? "" : $" ({pl.TruncationReason})")}");

var failed = false;
if (pl.Tracks.Count < minTracks) { Console.Error.WriteLine($"FAIL: expected at least {minTracks} tracks, got {pl.Tracks.Count}"); failed = true; }
if (expectComplete && pl.Truncated) { Console.Error.WriteLine("FAIL: result is truncated"); failed = true; }
if (pl.Tracks.Count > 0 && withAlbum < 90) { Console.Error.WriteLine($"FAIL: only {withAlbum}% of tracks have an album name"); failed = true; }
return failed ? 2 : 0;

/// <summary>Saves page server-data and API responses for building test fixtures. Never saves the JS bundle (it holds tokens).</summary>
sealed class DumpHandler(string dir, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _n;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var resp = await base.SendAsync(request, ct);
        var host = request.RequestUri!.Host;
        var path = request.RequestUri.AbsolutePath;
        if (path.EndsWith(".js", StringComparison.Ordinal)) return resp;

        await resp.Content.LoadIntoBufferAsync();
        // Redact anything that looks like a JWT so a token can never end up in a fixture.
        var body = System.Text.RegularExpressions.Regex.Replace(await resp.Content.ReadAsStringAsync(ct),
            @"eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", "REDACTED.JWT.TOKEN");
        Directory.CreateDirectory(dir);
        var n = Interlocked.Increment(ref _n);
        if (host == "music.apple.com")
        {
            File.WriteAllText(Path.Combine(dir, $"{n:00}-page-server-data.json"), PageParser.ExtractServerDataJson(body) ?? "");
            File.WriteAllText(Path.Combine(dir, $"{n:00}-page-head.html"), body.Split("</head>")[0]
                .Replace(PageParser.FindBundlePath(body) ?? "\0", "/assets/index~fixture.js"));
        }
        else
        {
            File.WriteAllText(Path.Combine(dir, $"{n:00}-api.json"), body);
        }
        return resp;
    }
}
