using System.Net;
using System.Text;

namespace AppleMusicPlaylist.Tests;

internal static class Fixture
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A fake, unsigned JWT. Never a real Apple token.</summary>
    public static string FakeJwt(string kid, string iss, DateTimeOffset exp)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64($"{{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"{kid}\"}}")}.{B64($"{{\"iss\":\"{iss}\",\"exp\":{exp.ToUnixTimeSeconds()}}}")}.ZmFrZXNpZw";
    }

    public static string WrapPage(string serverDataJson, string title = "Synthetic Playlist - Playlist - Apple Music") =>
        $"""
        <!DOCTYPE html><html><head><title>{title}</title>
        <meta name="apple:title" content="Synthetic Playlist">
        <script type="module" crossorigin src="/assets/index~abc123.js"></script>
        </head><body>
        <script type="application/json" id="serialized-server-data">{serverDataJson}</script>
        </body></html>
        """;
}

/// <summary>Serves canned responses by URL prefix and records requests.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly List<(string Prefix, HttpStatusCode Status, string Body)> _routes = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHandler On(string urlPrefix, string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.RemoveAll(r => r.Prefix == urlPrefix);
        _routes.Add((urlPrefix, status, body));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();
        // Longest matching prefix wins.
        var route = _routes.Where(r => url.StartsWith(r.Prefix, StringComparison.Ordinal)).OrderByDescending(r => r.Prefix.Length).FirstOrDefault();
        var resp = route.Prefix is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
            : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body) };
        return Task.FromResult(resp);
    }
}
