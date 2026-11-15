using System.Text.RegularExpressions;

namespace AppleMusicPlaylist;

/// <summary>Parses music.apple.com playlist URLs.</summary>
public static partial class PlaylistUrl
{
    // https://music.apple.com/us/playlist/<slug>/pl.xxx  (slug optional), query string ignored
    [GeneratedRegex(@"^https?://(?:embed\.)?music\.apple\.com/(?<sf>[a-z]{2})/playlist/(?:[^/?#]+/)?(?<id>pl\.[A-Za-z0-9\-_.]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static bool TryParse(string? url, out string storefront, out string id)
    {
        storefront = id = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var m = UrlRegex().Match(url.Trim());
        if (!m.Success) return false;
        storefront = m.Groups["sf"].Value.ToLowerInvariant();
        id = m.Groups["id"].Value;
        return true;
    }
}
