using System.Net;

namespace AppleMusicPlaylist.Tests;

public class LibraryTests
{
    private const string UserToken = "fake-user-token-SECRET";
    private const string Api = "https://amp-api.music.apple.com";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static FakeHandler Site() => new FakeHandler()
        .On("https://music.apple.com/us/browse", Fixture.WrapPage("{}"))
        .On("https://music.apple.com/assets/index~abc123.js", $"t=\"{Fixture.FakeJwt("WebPlayKid", "AMPWebPlay", Now.AddDays(30))}\"");

    private static AppleMusicPlaylistClient Client(FakeHandler h, AppleMusicPlaylistOptions? o = null) => new(new HttpClient(h), o, () => Now);

    private static string Songs(int start, int n, string? next) =>
        "{" + (next is null ? "" : $"\"next\":\"{next}\",") + "\"data\":[" +
        string.Join(",", Enumerable.Range(start, n).Select(i =>
            $"{{\"id\":\"i.{i}\",\"type\":\"library-songs\",\"attributes\":{{\"name\":\"Song {i}\",\"artistName\":\"Artist {i}\",\"albumName\":\"Album {i}\"}}}}")) + "]}";

    [Fact]
    public async Task Lists_library_playlists_across_pages_with_user_token_header()
    {
        var h = Site()
            .On($"{Api}/v1/me/library/playlists?limit=100", """{"next":"/v1/me/library/playlists?offset=100","data":[{"id":"p.A","type":"library-playlists","attributes":{"name":"Road Trip"}}]}""")
            .On($"{Api}/v1/me/library/playlists?offset=100", """{"data":[{"id":"p.B","type":"library-playlists","attributes":{"name":"Focus"}}]}""");

        var list = await Client(h).GetLibraryPlaylistsAsync(UserToken);

        Assert.Equal(new[] { new LibraryPlaylist("p.A", "Road Trip"), new LibraryPlaylist("p.B", "Focus") }, list);
        var api = h.Requests.Where(r => r.RequestUri!.Host == "amp-api.music.apple.com").ToList();
        Assert.Equal(2, api.Count);
        Assert.All(api, r =>
        {
            Assert.Equal(UserToken, r.Headers.GetValues("media-user-token").Single());
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal("https://music.apple.com", r.Headers.GetValues("Origin").Single());
        });
        Assert.DoesNotContain(h.Requests, r => r.RequestUri!.ToString().Contains(UserToken));
    }

    [Fact]
    public async Task Reads_library_playlist_tracks_across_pages()
    {
        var h = Site()
            .On($"{Api}/v1/me/library/playlists/p.A", """{"data":[{"id":"p.A","type":"library-playlists","attributes":{"name":"Road Trip"}}]}""")
            .On($"{Api}/v1/me/library/playlists/p.A/tracks?limit=100", Songs(0, 100, "/v1/me/library/playlists/p.A/tracks?offset=100"))
            .On($"{Api}/v1/me/library/playlists/p.A/tracks?offset=100", Songs(100, 7, null));

        var pl = await Client(h).GetLibraryPlaylistAsync(UserToken, "p.A");

        Assert.Equal("Road Trip", pl.Name);
        Assert.Equal("p.A", pl.Id);
        Assert.Equal(107, pl.Tracks.Count);
        Assert.False(pl.Truncated);
        Assert.Equal(new PlaylistTrack("Song 0", "Artist 0", "Album 0"), pl.Tracks[0]);
        Assert.All(pl.Tracks, t => Assert.Null(t.AlbumArtistName));
    }

    [Fact]
    public async Task Library_playlist_truncates_only_on_max_tracks()
    {
        var h = Site()
            .On($"{Api}/v1/me/library/playlists/p.A", """{"data":[{"id":"p.A","attributes":{"name":"Big"}}]}""")
            .On($"{Api}/v1/me/library/playlists/p.A/tracks?limit=100", Songs(0, 100, "/v1/me/library/playlists/p.A/tracks?offset=100"))
            .On($"{Api}/v1/me/library/playlists/p.A/tracks?offset=100", Songs(100, 100, null));

        var pl = await Client(h, new AppleMusicPlaylistOptions { MaxTracks = 150 }).GetLibraryPlaylistAsync(UserToken, "p.A");
        Assert.Equal(150, pl.Tracks.Count);
        Assert.True(pl.Truncated);
    }

    [Fact]
    public async Task Empty_library_playlist_returns_no_tracks()
    {
        var h = Site()
            .On($"{Api}/v1/me/library/playlists/p.E", """{"data":[{"id":"p.E","attributes":{"name":"Empty"}}]}""")
            .On($"{Api}/v1/me/library/playlists/p.E/tracks", "", HttpStatusCode.NotFound);

        var pl = await Client(h).GetLibraryPlaylistAsync(UserToken, "p.E");
        Assert.Empty(pl.Tracks);
        Assert.False(pl.Truncated);
    }

    [Fact]
    public async Task Missing_library_playlist_throws_404()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Client(Site()).GetLibraryPlaylistAsync(UserToken, "p.missing"));
        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Rejected_user_token_throws_auth_exception_without_the_token(HttpStatusCode status)
    {
        var h = Site().On($"{Api}/v1/me/library/playlists", "", status);
        var e = await Assert.ThrowsAsync<AppleMusicAuthException>(() => Client(h).GetLibraryPlaylistsAsync(UserToken));
        Assert.DoesNotContain(UserToken, e.ToString());
        Assert.Contains(((int)status).ToString(), e.Message);
    }

    [Fact]
    public async Task Blank_user_token_is_rejected_before_any_request()
    {
        var h = Site();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Client(h).GetLibraryPlaylistsAsync(" "));
        Assert.Empty(h.Requests);
    }
}
