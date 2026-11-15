# applemusic-playlist-scraper

A small .NET 8 library and CLI that reads a **public** Apple Music playlist (song title, artist,
album) from its `music.apple.com` link. No Apple account, developer membership or API key.

Built for [lidarr-plugin-applemusic](https://github.com/dadatuputi/lidarr-plugin-applemusic),
but usable on its own. It has no dependencies outside the .NET base library.

A [daily canary](.github/workflows/canary.yml) reads real public playlists, checks the library
endpoint still rejects a bad user token, and opens an issue
labelled `canary` when Apple changes something.

## Use it

```csharp
using AppleMusicPlaylist;

using var client = new AppleMusicPlaylistClient();          // or pass your own HttpClient and options
Playlist pl = await client.GetPlaylistAsync("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb");

Console.WriteLine($"{pl.Name}: {pl.Tracks.Count} tracks, truncated={pl.Truncated}");
foreach (PlaylistTrack t in pl.Tracks)
    Console.WriteLine($"{t.ArtistName} / {t.AlbumName} / {t.Title}");
```

| Type | Members |
|---|---|
| `AppleMusicPlaylistClient` | `GetPlaylistAsync(string url, CancellationToken ct = default)` → `Task<Playlist>` |
| `Playlist` | `Id`, `Storefront`, `Name`, `Curator?`, `Url`, `Tracks`, `Truncated`, `TruncationReason?` |
| `PlaylistTrack` | `Title`, `ArtistName`, `AlbumName?`, `AlbumArtistName?` (only for songs past 300; use `AlbumArtistName ?? ArtistName`) |
| `AppleMusicPlaylistOptions` | `FetchAllPages` (true), `MaxTracks` (5000), `PageSize` (100), `UserAgent`, `Language` ("en-US") |
| `PlaylistUrl` | `TryParse(url, out storefront, out id)` |

### Private library playlists (optional)

```csharp
var mine = await client.GetLibraryPlaylistsAsync(musicUserToken);        // LibraryPlaylist(Id "p.xxxx", Name)
Playlist pl = await client.GetLibraryPlaylistAsync(musicUserToken, mine[0].Id);
```

`musicUserToken` is the `media-user-token` of a signed-in music.apple.com session. It is a
credential to the user's Apple account: store it like a password. The library never logs it or
puts it in an exception. It expires after some months; Apple then answers 401/403 and the
library throws `AppleMusicAuthException`. Library playlists are never `Truncated` except at
`MaxTracks`, and their tracks have no `AlbumArtistName`.

Errors: `ArgumentException` for a non-playlist URL, `HttpRequestException` when the page can't be
fetched (a private or deleted playlist gives 404), `AppleMusicScrapeException` when the page no
longer has the expected shape. Failure to read past 300 tracks does **not** throw: you get the
first 300 with `Truncated = true` and a reason.

Music videos and other non-song items are skipped. Duplicates are kept in playlist order.

### CLI

```
dotnet run --project src/AppleMusicPlaylist.Cli -- <playlist-url> [--format text|json|albums] [--page-only]
                                                  [--min-tracks N] [--expect-complete] [--dump DIR]
```

Writes tracks to stdout and a one-line summary to stderr. `--min-tracks` and `--expect-complete`
make it exit 2 on a bad result (used by the canary). `--dump` saves the page data and API
responses for building test fixtures, with any JWT replaced by `REDACTED.JWT.TOKEN`.

### As a git submodule

```
git submodule add https://github.com/dadatuputi/applemusic-playlist-scraper Submodules/applemusic-playlist-scraper
```

then reference `Submodules/applemusic-playlist-scraper/src/AppleMusicPlaylist/AppleMusicPlaylist.csproj`.

## How it works

1. `GET https://music.apple.com/<storefront>/playlist/<id>` with a browser User-Agent. The page
   embeds JSON in `<script id="serialized-server-data">`. Song rows are objects with
   `contentDescriptor.kind == "song"` inside sections whose `id` starts with `track-list`:
   `title`, `artistName`, and the album in `tertiaryLinks[0].title`.
2. The page holds at most 300 songs. A longer playlist has a `nextIntent.url` like
   `/v1/catalog/us/playlists/<id>/tracks?offset=300`.
3. To read the rest, the library loads the site's JS bundle (`/assets/index~<hash>.js`), takes the
   web player's public JWT from it (header `kid` `WebPlayKid`), and pages through
   `https://amp-api.music.apple.com/v1/catalog/...` 100 tracks at a time, following `next`.
   The token is fetched fresh on each call and never stored.

## Caveats

- Step 3 uses Apple's own web-player token and an undocumented API. It may break or conflict
  with Apple's terms; `music.apple.com/robots.txt` disallows `/v1/*`. Set
  `FetchAllPages = false` to read only the page (first 300 songs). One playlist a day is a very
  light load.
- Without a user token, only public playlists work. Private library access sends the user's own
  token to Apple's `/v1/me/library` endpoints, the same calls the web player makes.
- The official route is MusicKit / the Apple Music API, which needs a paid Apple Developer
  membership.
- GitHub pauses scheduled workflows in a repository with no activity for 60 days. If the canary
  stops, re-enable it from the Actions tab.

## Develop

Needs the .NET 8 SDK (CI installs it; you don't need it locally).

```
dotnet test
```

Unit tests run offline on files in `tests/AppleMusicPlaylist.Tests/Fixtures`. No real tokens or
JS bundles are committed.
