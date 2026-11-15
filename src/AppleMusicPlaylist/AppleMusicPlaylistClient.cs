using System.Net.Http.Headers;

namespace AppleMusicPlaylist;

/// <summary>
/// Reads public Apple Music playlists without an Apple account or developer token.
/// <para>
/// The playlist web page holds the first 300 tracks. Longer playlists are read from Apple's
/// catalog API using the web player's own public token, taken from the site's JS bundle on each
/// run. That API is undocumented: if it fails, the first 300 tracks are returned with
/// <see cref="Playlist.Truncated"/> set.
/// </para>
/// </summary>
public sealed class AppleMusicPlaylistClient : IDisposable
{
    public const string WebBase = "https://music.apple.com";
    public const string ApiBase = "https://amp-api.music.apple.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AppleMusicPlaylistOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="httpClient">Optional client to reuse (not disposed by this class).</param>
    /// <param name="options">Optional settings.</param>
    public AppleMusicPlaylistClient(HttpClient? httpClient = null, AppleMusicPlaylistOptions? options = null)
        : this(httpClient, options, () => DateTimeOffset.UtcNow) { }

    internal AppleMusicPlaylistClient(HttpClient? httpClient, AppleMusicPlaylistOptions? options, Func<DateTimeOffset> clock)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _options = options ?? new AppleMusicPlaylistOptions();
        _clock = clock;
    }

    /// <summary>Reads a public playlist from its music.apple.com URL.</summary>
    /// <exception cref="ArgumentException">The URL is not an Apple Music playlist URL.</exception>
    /// <exception cref="HttpRequestException">The playlist page could not be fetched (404 for private or deleted playlists).</exception>
    /// <exception cref="AppleMusicScrapeException">The page no longer has the expected structure.</exception>
    public async Task<Playlist> GetPlaylistAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!PlaylistUrl.TryParse(url, out var storefront, out var id))
            throw new ArgumentException($"Not an Apple Music playlist URL: {url}", nameof(url));

        var pageUrl = $"{WebBase}/{storefront}/playlist/{id}";
        var html = await GetStringAsync(pageUrl, api: false, token: null, cancellationToken).ConfigureAwait(false);
        var page = PageParser.ParsePage(html);

        var tracks = new List<PlaylistTrack>(page.Tracks);
        bool truncated = false;
        string? reason = null;

        if (page.NextPath is not null)
        {
            if (!_options.FetchAllPages)
            {
                (truncated, reason) = (true, "Paging disabled; only the first page of tracks was read.");
            }
            else
            {
                try
                {
                    var hitLimit = await ReadRemainingAsync(html, page.NextPath, tracks, cancellationToken).ConfigureAwait(false);
                    if (hitLimit) (truncated, reason) = (true, $"Stopped at MaxTracks ({_options.MaxTracks}).");
                }
                catch (Exception e) when (e is HttpRequestException or AppleMusicScrapeException or System.Text.Json.JsonException
                                              || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    (truncated, reason) = (true, $"Could not read past track {tracks.Count}: {e.Message}");
                }
            }
        }

        if (tracks.Count > _options.MaxTracks)
        {
            tracks.RemoveRange(_options.MaxTracks, tracks.Count - _options.MaxTracks);
            (truncated, reason) = (true, $"Stopped at MaxTracks ({_options.MaxTracks}).");
        }

        return new Playlist
        {
            Id = id,
            Storefront = storefront,
            Name = page.Name ?? id,
            Curator = page.Curator,
            Url = pageUrl,
            Tracks = tracks,
            Truncated = truncated,
            TruncationReason = reason,
        };
    }

    /// <summary>
    /// Lists the playlists in the user's own Apple Music library.
    /// </summary>
    /// <param name="musicUserToken">The <c>media-user-token</c> of a signed-in music.apple.com session. Never logged.</param>
    /// <exception cref="AppleMusicAuthException">Apple rejected the token (expired or revoked).</exception>
    public async Task<IReadOnlyList<LibraryPlaylist>> GetLibraryPlaylistsAsync(string musicUserToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(musicUserToken);
        var token = await GetDeveloperTokenAsync(null, cancellationToken).ConfigureAwait(false);

        var result = new List<LibraryPlaylist>();
        string? next = "/v1/me/library/playlists?limit=100";
        for (var guard = 0; next is not null; guard++)
        {
            if (guard > 1000) throw new AppleMusicScrapeException("Paging did not terminate.");
            var json = await GetLibraryAsync(next, token, musicUserToken, cancellationToken).ConfigureAwait(false);
            var (page, nextPath) = PageParser.ParseLibraryPlaylists(json);
            result.AddRange(page);
            next = nextPath;
        }
        return result;
    }

    /// <summary>
    /// Reads one playlist from the user's own library. <see cref="Playlist.Truncated"/> is set only
    /// when <see cref="AppleMusicPlaylistOptions.MaxTracks"/> is reached. Library songs have no
    /// album artist, so <see cref="PlaylistTrack.AlbumArtistName"/> is null.
    /// </summary>
    /// <param name="musicUserToken">The <c>media-user-token</c> of a signed-in music.apple.com session. Never logged.</param>
    /// <param name="libraryPlaylistId">Library playlist id, e.g. <c>p.AbCdEf</c>.</param>
    /// <exception cref="AppleMusicAuthException">Apple rejected the token (expired or revoked).</exception>
    /// <exception cref="HttpRequestException">The playlist does not exist (404).</exception>
    public async Task<Playlist> GetLibraryPlaylistAsync(string musicUserToken, string libraryPlaylistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(musicUserToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPlaylistId);
        var token = await GetDeveloperTokenAsync(null, cancellationToken).ConfigureAwait(false);
        var id = Uri.EscapeDataString(libraryPlaylistId.Trim());

        var meta = await GetLibraryAsync($"/v1/me/library/playlists/{id}", token, musicUserToken, cancellationToken).ConfigureAwait(false);
        var name = PageParser.ParseLibraryPlaylists(meta).Playlists.FirstOrDefault()?.Name ?? libraryPlaylistId;

        var tracks = new List<PlaylistTrack>();
        bool truncated = false;
        try
        {
            truncated = await ReadPagesAsync($"/v1/me/library/playlists/{id}/tracks?limit={_options.PageSize}", token, musicUserToken, tracks, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound && tracks.Count == 0)
        {
            // Apple answers 404 for the tracks of an empty library playlist (the playlist itself exists, checked above).
        }

        if (tracks.Count > _options.MaxTracks)
        {
            tracks.RemoveRange(_options.MaxTracks, tracks.Count - _options.MaxTracks);
            truncated = true;
        }

        return new Playlist
        {
            Id = libraryPlaylistId.Trim(),
            Storefront = "",
            Name = name,
            Curator = null,
            Url = $"{WebBase}/library/playlist/{id}",
            Tracks = tracks,
            Truncated = truncated,
            TruncationReason = truncated ? $"Stopped at MaxTracks ({_options.MaxTracks})." : null,
        };
    }

    /// <summary>Takes the web player's public token from the site's JS bundle.</summary>
    /// <param name="html">A music.apple.com page already fetched, or null to fetch one.</param>
    private async Task<string> GetDeveloperTokenAsync(string? html, CancellationToken ct)
    {
        html ??= await GetStringAsync($"{WebBase}/us/browse", api: false, token: null, ct).ConfigureAwait(false);
        var bundlePath = PageParser.FindBundlePath(html)
            ?? throw new AppleMusicScrapeException("Page has no /assets/index*.js bundle reference.");
        var js = await GetStringAsync(WebBase + bundlePath, api: false, token: null, ct).ConfigureAwait(false);
        return PageParser.FindWebPlayToken(js, _clock())
            ?? throw new AppleMusicScrapeException("No unexpired web-player token found in the JS bundle.");
    }

    /// <returns>True if <see cref="AppleMusicPlaylistOptions.MaxTracks"/> stopped the read early.</returns>
    private async Task<bool> ReadRemainingAsync(string html, string nextPath, List<PlaylistTrack> tracks, CancellationToken ct)
    {
        var token = await GetDeveloperTokenAsync(html, ct).ConfigureAwait(false);
        return await ReadPagesAsync(nextPath, token, null, tracks, ct).ConfigureAwait(false);
    }

    /// <returns>True if <see cref="AppleMusicPlaylistOptions.MaxTracks"/> stopped the read early.</returns>
    private async Task<bool> ReadPagesAsync(string firstPath, string token, string? userToken, List<PlaylistTrack> tracks, CancellationToken ct)
    {
        string? next = firstPath;
        var guard = 0;
        while (next is not null)
        {
            if (tracks.Count >= _options.MaxTracks) return true;
            if (++guard > 1000) throw new AppleMusicScrapeException("Paging did not terminate.");

            var resp = userToken is null
                ? await GetStringAsync(ApiBase + WithQuery(next), api: true, token, ct).ConfigureAwait(false)
                : await GetLibraryAsync(next, token, userToken, ct).ConfigureAwait(false);
            var page = PageParser.ParseTracksApi(resp);
            tracks.AddRange(page.Tracks);
            next = page.NextPath;
        }
        return false;
    }

    private async Task<string> GetLibraryAsync(string path, string token, string userToken, CancellationToken ct)
    {
        try
        {
            return await GetStringAsync(ApiBase + WithQuery(path), api: true, token, ct, userToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new AppleMusicAuthException(
                $"Apple Music rejected the Music User Token ({(int)e.StatusCode!}). It has probably expired; sign in at music.apple.com and copy a new one.");
        }
    }

    private string WithQuery(string path)
    {
        if (!path.Contains("l=", StringComparison.Ordinal)) path += (path.Contains('?') ? "&" : "?") + "l=" + _options.Language;
        if (!path.Contains("limit=", StringComparison.Ordinal)) path += "&limit=" + _options.PageSize;
        return path;
    }

    private async Task<string> GetStringAsync(string url, bool api, string? token, CancellationToken ct, string? userToken = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", _options.Language + ",en;q=0.8");
        if (api)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Origin", WebBase);
            req.Headers.TryAddWithoutValidation("Referer", WebBase + "/");
            req.Headers.Accept.ParseAdd("application/json");
            if (userToken is not null) req.Headers.TryAddWithoutValidation("media-user-token", userToken);
        }
        else
        {
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        }

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {StripQuery(url)} returned {(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string StripQuery(string url) => url.Split('?')[0];

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
