namespace AppleMusicPlaylist.Tests;

public class PlaylistUrlTests
{
    [Theory]
    [InlineData("https://music.apple.com/us/playlist/favorite-songs/pl.u-BpUermdLkZ", "us", "pl.u-BpUermdLkZ")]
    [InlineData("https://music.apple.com/gb/playlist/pl.f4d106fed2bd41149aaacabb233eb5eb?l=en", "gb", "pl.f4d106fed2bd41149aaacabb233eb5eb")]
    [InlineData("  https://MUSIC.apple.com/US/playlist/x/pl.abc  ", "us", "pl.abc")]
    [InlineData("https://embed.music.apple.com/us/playlist/x/pl.abc", "us", "pl.abc")]
    public void Parses_valid_urls(string url, string sf, string id)
    {
        Assert.True(PlaylistUrl.TryParse(url, out var s, out var i));
        Assert.Equal(sf, s);
        Assert.Equal(id, i);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://music.apple.com/us/album/x/123")]
    [InlineData("https://example.com/us/playlist/x/pl.abc")]
    public void Rejects_other_urls(string url) => Assert.False(PlaylistUrl.TryParse(url, out _, out _));
}

public class PageParserTests
{
    [Fact]
    public void Synthetic_server_data_yields_songs_header_and_next()
    {
        var page = PageParser.ParseServerData(Fixture.Read("synthetic-server-data.json"));

        Assert.Equal("Synthetic Playlist", page.Name);
        Assert.Equal("Test Curator", page.Curator);
        Assert.Equal(299, page.Tracks.Count); // 300 items, one music video skipped
        Assert.Equal(new PlaylistTrack("Song 1", "Artist 1", "Album 1"), page.Tracks[0]);
        Assert.Null(page.Tracks.Single(t => t.Title == "Song 5").AlbumName);
        Assert.DoesNotContain(page.Tracks, t => t.Title == "Song 10" || t.Title == "Other Playlist");
        Assert.Equal("/v1/catalog/us/playlists/pl.synthetic/tracks?l=en-US&offset=300", page.NextPath);
    }

    [Fact]
    public void ParsePage_reads_html_and_falls_back_to_meta_title()
    {
        var json = Fixture.Read("synthetic-server-data.json").Replace("\"Synthetic Playlist\"", "\"\"");
        var page = PageParser.ParsePage(Fixture.WrapPage(json));
        Assert.Equal("Synthetic Playlist", page.Name);
        Assert.Equal(299, page.Tracks.Count);
    }

    [Fact]
    public void ParsePage_without_server_data_throws()
    {
        Assert.Throws<AppleMusicScrapeException>(() => PageParser.ParsePage("<html><body>nope</body></html>"));
    }

    [Fact]
    public void Tracks_api_page_parses_songs_and_next()
    {
        var page = PageParser.ParseTracksApi(Fixture.Read("synthetic-api-300.json"));
        Assert.Equal(100, page.Tracks.Count);
        Assert.Equal(1, page.SkippedItems);
        Assert.Equal(new PlaylistTrack("Paged 300", "Artist 0", "Album 0"), page.Tracks[0]);
        Assert.Equal("/v1/catalog/us/playlists/pl.synthetic/tracks?offset=400", page.NextPath);

        var last = PageParser.ParseTracksApi(Fixture.Read("synthetic-api-400.json"));
        Assert.Equal(13, last.Tracks.Count);
        Assert.Null(last.NextPath);
    }

    [Fact]
    public void Finds_bundle_path()
    {
        Assert.Equal("/assets/index~abc123.js", PageParser.FindBundlePath(Fixture.WrapPage("{}")));
        Assert.Null(PageParser.FindBundlePath("<html></html>"));
    }

    [Fact]
    public void Finds_unexpired_web_play_token()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = Fixture.FakeJwt("WebPlayKid", "AMPWebPlay", now.AddDays(-1));
        var other = Fixture.FakeJwt("OtherKid", "SomeoneElse", now.AddDays(30));
        var good = Fixture.FakeJwt("WebPlayKid", "AMPWebPlay", now.AddDays(30));
        var js = $"var a=\"{expired}\";var b='{other}';const c=\"{good}\";";

        Assert.Equal(good, PageParser.FindWebPlayToken(js, now));
        Assert.Null(PageParser.FindWebPlayToken($"x=\"{expired}\";y=\"{other}\"", now));
    }

    [Fact]
    public void Falls_back_to_issuer_when_kid_differs()
    {
        var now = DateTimeOffset.UtcNow;
        var byIss = Fixture.FakeJwt("NewKid", "AMPWebPlay", now.AddDays(30));
        Assert.Equal(byIss, PageParser.FindWebPlayToken($"t=\"{byIss}\"", now));
    }
}
