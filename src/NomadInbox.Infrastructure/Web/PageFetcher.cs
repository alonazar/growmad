using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace NomadInbox.Infrastructure.Web;

public sealed record FetchedPage(Uri FinalUrl, string ContentType, string? Charset, byte[] Body);

/// <summary>A link that can't be fetched, with a message meant for the UI.</summary>
public sealed class LinkFetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads linked pages. Connections to loopback/private/link-local addresses are refused unless allowed,
/// checked when the socket connects, so redirects and DNS tricks can't reach NOMAD's own services or the LAN.
/// </summary>
public sealed class PageFetcher : IDisposable
{
    public const int MaxBytes = 10 * 1024 * 1024;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private readonly HttpClient _publicOnly = Create(allowPrivate: false);
    private readonly HttpClient _any = Create(allowPrivate: true);

    /// <param name="maxBytes">Size limit for this download (pages: <see cref="MaxBytes"/>; media can allow more).</param>
    /// <param name="referrer">Page the request comes from; some sites refuse image requests without it.</param>
    public async Task<FetchedPage> FetchAsync(string url, bool allowPrivateNetwork, CancellationToken ct,
        long maxBytes = MaxBytes, string? referrer = null)
    {
        var client = allowPrivateNetwork ? _any : _publicOnly;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referrer is not null && Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri)) request.Headers.Referrer = referrerUri;

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException blocked)
        {
            throw new LinkFetchException(blocked.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LinkFetchException($"Could not connect: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LinkFetchException("The site took too long to respond.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new LinkFetchException($"The site answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            if (response.Content.Headers.ContentLength > maxBytes)
                throw new LinkFetchException($"Larger than {maxBytes / 1024 / 1024} MB.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new LinkFetchException($"Larger than {maxBytes / 1024 / 1024} MB.");
            }

            var type = response.Content.Headers.ContentType;
            return new FetchedPage(
                response.RequestMessage?.RequestUri ?? new Uri(url),
                type?.MediaType ?? "application/octet-stream",
                type?.CharSet,
                buffer.ToArray());
        }
    }

    public void Dispose()
    {
        _publicOnly.Dispose();
        _any.Dispose();
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                     || b[0] == 0
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)            // link-local
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // carrier-grade NAT
                     || b[0] >= 224);                             // multicast / reserved
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || (address.GetAddressBytes()[0] & 0xFE) == 0xFC); // unique local fc00::/7
    }

    private static HttpClient Create(bool allowPrivate)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = allowPrivate ? addresses : addresses.Where(IsPublic).ToArray();
                if (allowed.Length == 0)
                    throw new BlockedAddressException(
                        $"{context.DnsEndPoint.Host} is on a local network. Turn on \"Allow links to local-network addresses\" in Settings to fetch it.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/pdf;q=0.9,text/plain;q=0.8,*/*;q=0.5");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en"));
        return client;
    }

    private sealed class BlockedAddressException(string message) : Exception(message);
}
