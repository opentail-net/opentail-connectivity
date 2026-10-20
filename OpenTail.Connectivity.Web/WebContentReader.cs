using System.Net;

namespace OpenTail.Connectivity.Web;

internal static class WebContentReader
{
    private const int MaxRedirects = 5;

    public static async Task<HttpResponseMessage?> GetWithinOriginAsync(
        HttpClient client, Uri uri, Uri origin, CancellationToken cancellationToken, Func<Uri, bool>? allowRedirectTo = null)
    {
        var current = uri;
        for (var redirects = 0; ;)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
            {
                var finalUri = response.RequestMessage?.RequestUri ?? current;
                if (SiteCrawlConnector.HasSameOrigin(finalUri, origin))
                    return response;
                response.Dispose();
                return null;
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
            if (!SiteCrawlConnector.HasSameOrigin(next, origin) || allowRedirectTo?.Invoke(next) == false || redirects == MaxRedirects)
                return null;
            current = next;
            redirects++;
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maxBytes)
            return null;

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 81_920));
        var chunk = new byte[81_920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }
}
