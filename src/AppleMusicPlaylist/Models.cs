namespace AppleMusicPlaylist;

/// <summary>One song in an Apple Music playlist.</summary>
/// <param name="Title">Song title.</param>
/// <param name="ArtistName">Apple's display artist; a duet reads "A &amp; B".</param>
/// <param name="AlbumName">Album title, or null if Apple gives none.</param>
/// <param name="AlbumArtistName">
/// The album's artist when known. Only songs read through the paging API (past the first 300) have it;
/// songs from the HTML page leave it null. Use <c>AlbumArtistName ?? ArtistName</c> for album lookups.
/// </param>
public sealed record PlaylistTrack(string Title, string ArtistName, string? AlbumName, string? AlbumArtistName = null);

/// <summary>A public Apple Music playlist as read from music.apple.com.</summary>
public sealed record Playlist
{
    /// <summary>Playlist id, e.g. <c>pl.u-BpUermdLkZ</c> or <c>pl.f4d1...</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Two-letter storefront from the URL, e.g. <c>us</c>.</summary>
    public required string Storefront { get; init; }

    public required string Name { get; init; }

    /// <summary>Curator or owner name when the page exposes one.</summary>
    public string? Curator { get; init; }

    public required string Url { get; init; }

    /// <summary>Songs in playlist order. Music videos and other non-song items are skipped.</summary>
    public required IReadOnlyList<PlaylistTrack> Tracks { get; init; }

    /// <summary>
    /// True when the playlist has more tracks than were returned (the page holds 300 and paging
    /// past that failed, was disabled, or hit <see cref="AppleMusicPlaylistOptions.MaxTracks"/>).
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Why <see cref="Truncated"/> is true; null otherwise.</summary>
    public string? TruncationReason { get; init; }
}

public sealed class AppleMusicPlaylistOptions
{
    /// <summary>Read past the first 300 tracks with Apple's web-player API. Default true.</summary>
    public bool FetchAllPages { get; set; } = true;

    /// <summary>Hard stop on track count. Default 5000.</summary>
    public int MaxTracks { get; set; } = 5000;

    /// <summary>Page size for the paging API (Apple allows up to 100). Default 100.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Browser-like User-Agent; music.apple.com serves the full page only to browsers.</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    /// <summary>Language passed to the API as <c>l=</c>. Default en-US.</summary>
    public string Language { get; set; } = "en-US";
}

/// <summary>A playlist in the signed-in user's own library (id like <c>p.xxxx</c>).</summary>
public sealed record LibraryPlaylist(string Id, string Name);

/// <summary>
/// Thrown when Apple rejects the Music User Token (HTTP 401 or 403), usually because it expired.
/// The message never contains the token.
/// </summary>
public sealed class AppleMusicAuthException : Exception
{
    public AppleMusicAuthException(string message) : base(message) { }
}

/// <summary>Thrown when the page or API no longer has the shape this library expects.</summary>
public sealed class AppleMusicScrapeException : Exception
{
    public AppleMusicScrapeException(string message, Exception? inner = null) : base(message, inner) { }
}
