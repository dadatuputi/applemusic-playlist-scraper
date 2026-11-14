namespace AppleMusicPlaylist.Tests;

/// <summary>
/// Tests on responses captured from music.apple.com on 2026-11-14 (trimmed of artwork and
/// metrics; no tokens). If Apple changes the page, the live canary fails first; refresh these
/// with the CLI's --dump option.
/// </summary>
public class RealFixtureTests
{
    private const string TenYearsId = "pl.c125e4b2998a4015abbe6b9ae90d5479";

    [Fact]
    public void Todays_hits_page_has_50_songs_and_no_next()
    {
        var page = PageParser.ParseServerData(Fixture.Read("todays-hits-server-data.json"));

        Assert.Equal("Today’s Hits", page.Name);
        Assert.Equal("Apple Music Hits", page.Curator);
        Assert.Equal(50, page.Tracks.Count);
        Assert.Null(page.NextPath);
        Assert.Equal(new PlaylistTrack("Housewife", "Tate McRae", "Housewife - Single"), page.Tracks[0]);
        Assert.Equal("Drake & Don Toliver", page.Tracks.Single(t => t.Title == "Solar Eclipse").ArtistName);
        Assert.All(page.Tracks, t => Assert.NotNull(t.AlbumName));
    }

    [Fact]
    public void Long_playlist_page_has_300_songs_and_next_intent()
    {
        var page = PageParser.ParseServerData(Fixture.Read("ten-years-server-data.json"));

        Assert.Equal("10 Years of Apple Music: Top Songs", page.Name);
        Assert.Equal(300, page.Tracks.Count);
        Assert.Equal($"/v1/catalog/us/playlists/{TenYearsId}/tracks?l=en-US&offset=300", page.NextPath);
        Assert.Equal(new PlaylistTrack("Espresso", "Sabrina Carpenter", "Espresso - Single"), page.Tracks[^1]);
    }

    [Fact]
    public void Api_pages_continue_to_500()
    {
        var p1 = PageParser.ParseTracksApi(Fixture.Read("ten-years-api-300.json"));
        Assert.Equal(100, p1.Tracks.Count);
        Assert.Equal($"/v1/catalog/us/playlists/{TenYearsId}/tracks?l=en-US&offset=400", p1.NextPath);
        Assert.Equal(new PlaylistTrack("Sweet Child O' Mine", "Guns N' Roses", "Appetite For Destruction (Super Deluxe)"), p1.Tracks[0]);

        var p2 = PageParser.ParseTracksApi(Fixture.Read("ten-years-api-400.json"));
        Assert.Equal(100, p2.Tracks.Count);
        Assert.Null(p2.NextPath);
        Assert.Equal(new PlaylistTrack("Fair Trade (feat. Travis Scott)", "Drake", "Certified Lover Boy"), p2.Tracks[^1]);
    }

    [Fact]
    public async Task Client_reads_all_500_from_real_fixtures()
    {
        var now = DateTimeOffset.UtcNow;
        var h = new FakeHandler()
            .On($"https://music.apple.com/us/playlist/{TenYearsId}", Fixture.WrapPage(Fixture.Read("ten-years-server-data.json")))
            .On("https://music.apple.com/assets/index~abc123.js", $"x=\"{Fixture.FakeJwt("WebPlayKid", "AMPWebPlay", now.AddDays(30))}\"")
            .On($"https://amp-api.music.apple.com/v1/catalog/us/playlists/{TenYearsId}/tracks?l=en-US&offset=300", Fixture.Read("ten-years-api-300.json"))
            .On($"https://amp-api.music.apple.com/v1/catalog/us/playlists/{TenYearsId}/tracks?l=en-US&offset=400", Fixture.Read("ten-years-api-400.json"));
        using var client = new AppleMusicPlaylistClient(new HttpClient(h), null, () => now);

        var pl = await client.GetPlaylistAsync($"https://music.apple.com/us/playlist/10-years-of-apple-music-top-songs/{TenYearsId}");

        Assert.Equal(500, pl.Tracks.Count);
        Assert.False(pl.Truncated);
        Assert.Equal("Apple Music", pl.Curator);
    }
}
