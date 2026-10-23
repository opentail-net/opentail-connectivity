using System.Net;
using System.Net.Sockets;

namespace OpenTail.Connectivity.Web;

/// <summary>
/// The HTTP stack the web connectors build on: no automatic redirects (each hop is checked before it is requested), and, unless
/// private networks are allowed, a connection only to public addresses. The address check runs on the address actually connected
/// to, after DNS resolution, so a name that resolves to a public address when checked and a private one when connecting (DNS
/// rebinding) cannot reach an internal service. Same rules as the Host's guarded clients.
/// </summary>
public static class WebNetwork
{
    /// <summary>A handler for the web connectors: redirects off, decompression on, private addresses refused unless allowed.</summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivateNetworks = false)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        if (!allowPrivateNetworks)
            handler.ConnectCallback = ConnectPubliclyAsync;
        return handler;
    }

    /// <summary>True only for addresses on the public internet: not loopback, private ranges, link-local (cloud metadata),
    /// carrier-grade NAT, multicast, reserved, or IPv6 local, unique-local, Teredo or 6to4 (which can embed a private IPv4).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 100 && b[1] is >= 64 and <= 127)            // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                          // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19)                     // benchmarking
                || b[0] >= 224);                                         // multicast, reserved, broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal || ip.IsIPv6Teredo) return false;
            var b = ip.GetAddressBytes();
            if (b[0] == 0x20 && b[1] == 0x02) return false;              // 6to4 can embed private IPv4
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks a caller's handler chain: refuses any handler that follows redirects itself, and attaches the public-address check to
    /// the <see cref="SocketsHttpHandler"/> at its end. A handler that opens no sockets of its own (a test stub) passes as it is.
    /// </summary>
    internal static void Prepare(HttpMessageHandler handler, bool allowPrivateNetworks)
    {
        ArgumentNullException.ThrowIfNull(handler);
        HttpMessageHandler? current = handler;
        while (current is not null)
        {
            switch (current)
            {
                case HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true }:
                    throw new ArgumentException("The handler must have AllowAutoRedirect = false so each redirect can be checked before it is followed.", nameof(handler));
                case SocketsHttpHandler sockets when !allowPrivateNetworks:
                    if (sockets.ConnectCallback is not null && sockets.ConnectCallback != ConnectPubliclyAsync)
                        throw new ArgumentException("The handler already has its own ConnectCallback, so private addresses cannot be refused; allow private networks or drop the callback.", nameof(handler));
                    sockets.ConnectCallback = ConnectPubliclyAsync;
                    break;
                case HttpClientHandler when !allowPrivateNetworks:
                    throw new ArgumentException("An HttpClientHandler cannot refuse private addresses; use a SocketsHttpHandler (WebNetwork.CreateHandler) or allow private networks.", nameof(handler));
            }

            current = (current as DelegatingHandler)?.InnerHandler;
        }
    }

    private static async ValueTask<Stream> ConnectPubliclyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        // Every address must be public, not just the one picked: otherwise a name with a public and a private address could be
        // steered to the private one.
        if (addresses.Length == 0)
            throw new HttpRequestException($"{host} did not resolve.");
        if (addresses.FirstOrDefault(a => !IsPublic(a)) is { } blocked)
            throw new HttpRequestException($"{host} resolves to {blocked}, a private or local address; set AllowPrivateNetworks to crawl an internal site.");

        return await ConnectFirstAsync(host, addresses, context.DnsEndPoint.Port, ConnectSocketAsync, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries the (already checked) addresses in the order DNS gave them until one connects, as the default handler would: a site
    /// whose first address is unreachable (an IPv6 address on a host without an IPv6 route) is still reached on the next.
    /// </summary>
    internal static async ValueTask<Stream> ConnectFirstAsync(string host, IReadOnlyList<IPAddress> addresses, int port,
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect, CancellationToken ct)
    {
        List<Exception>? failures = null;
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
                continue;
            try
            {
                return await connect(address, port, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or IOException && !ct.IsCancellationRequested)
            {
                (failures ??= []).Add(ex);
            }
        }
        throw new HttpRequestException($"Could not connect to {host} on any of its {addresses.Count} address(es).",
            failures is null ? null : new AggregateException(failures));
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
