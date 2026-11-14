using System.Net;

namespace AppleMusicPlaylist.Tests;

public class ClientTests
{
    private const string Url = "https://music.apple.com/us/playlist/synthetic/pl.synthetic";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static FakeHandler FullSite(string? bundle = null) => new FakeHandler()
        .On("https://music.apple.com/us/playlist/pl.synthetic", Fixture.WrapPage(Fixture.Read("synthetic-server-data.json")))
        .On("https://music.apple.com/assets/index~abc123.js", bundle ?? $"let t=\"{Fixture.FakeJwt("WebPlayKid", "AMPWebPlay", Now.AddDays(30))}\";")
        .On("https://amp-api.music.apple.com/v1/catalog/us/playlists/pl.synthetic/tracks?l=en-US&offset=300", Fixture.Read("synthetic-api-300.json"))
        .On("https://amp-api.music.apple.com/v1/catalog/us/playlists/pl.synthetic/tracks?offset=400", Fixture.Read("synthetic-api-400.json"));

    private static AppleMusicPlaylistClient Client(FakeHandler h, AppleMusicPlaylistOptions? o = null) =>
        new(new HttpClient(h), o, () => Now);

    [Fact]
    public async Task Reads_page_then_pages_through_api()
    {
        var h = FullSite();
        var pl = await Client(h).GetPlaylistAsync(Url);

        Assert.Equal("Synthetic Playlist", pl.Name);
        Assert.Equal("Test Curator", pl.Curator);
        Assert.Equal("pl.synthetic", pl.Id);
        Assert.Equal("us", pl.Storefront);
        Assert.Equal(299 + 100 + 13, pl.Tracks.Count);
        Assert.False(pl.Truncated);
        Assert.Null(pl.TruncationReason);
        Assert.Equal("Paged 412", pl.Tracks[^1].Title);

        var api = h.Requests.Where(r => r.RequestUri!.Host == "amp-api.music.apple.com").ToList();
        Assert.Equal(2, api.Count);
        Assert.All(api, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal("https://music.apple.com", r.Headers.GetValues("Origin").Single());
        });
        Assert.Contains("limit=100", api[0].RequestUri!.Query);
        Assert.Contains("l=en-US", api[1].RequestUri!.Query);
    }

    [Fact]
    public async Task Missing_token_returns_first_page_marked_truncated()
    {
        var pl = await Client(FullSite(bundle: "no tokens here")).GetPlaylistAsync(Url);
        Assert.Equal(299, pl.Tracks.Count);
        Assert.True(pl.Truncated);
        Assert.Contains("token", pl.TruncationReason);
    }

    [Fact]
    public async Task Api_error_returns_what_was_read_marked_truncated()
    {
        var h = FullSite().On("https://amp-api.music.apple.com/v1/catalog/us/playlists/pl.synthetic/tracks?offset=400", "", HttpStatusCode.Unauthorized);
        var pl = await Client(h).GetPlaylistAsync(Url);
        Assert.Equal(399, pl.Tracks.Count);
        Assert.True(pl.Truncated);
        Assert.Contains("401", pl.TruncationReason);
    }

    [Fact]
    public async Task Paging_disabled_reads_only_the_page()
    {
        var h = FullSite();
        var pl = await Client(h, new AppleMusicPlaylistOptions { FetchAllPages = false }).GetPlaylistAsync(Url);
        Assert.Equal(299, pl.Tracks.Count);
        Assert.True(pl.Truncated);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task MaxTracks_caps_the_result()
    {
        var pl = await Client(FullSite(), new AppleMusicPlaylistOptions { MaxTracks = 350 }).GetPlaylistAsync(Url);
        Assert.Equal(350, pl.Tracks.Count);
        Assert.True(pl.Truncated);
    }

    [Fact]
    public async Task Short_playlist_makes_one_request()
    {
        var json = Fixture.Read("synthetic-server-data.json").Replace("\"nextIntent\"", "\"notNext\"");
        var h = new FakeHandler().On("https://music.apple.com/us/playlist/pl.synthetic", Fixture.WrapPage(json));
        var pl = await Client(h).GetPlaylistAsync(Url);
        Assert.Equal(299, pl.Tracks.Count);
        Assert.False(pl.Truncated);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Missing_playlist_throws_http_404()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Client(new FakeHandler()).GetPlaylistAsync(Url));
        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode);
    }

    [Fact]
    public async Task Bad_url_throws_argument_exception()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client(new FakeHandler()).GetPlaylistAsync("https://example.com"));
    }
}
