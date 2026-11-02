using System.Net;
using System.Net.Sockets;
using Gelato.Config;

namespace Gelato.Services;

/// <summary>
/// Which hosts the Jellyfin server may fetch a stream from (lostb1t/Gelato#356). The server
/// fetches a stream link itself to probe, check, download and remux it, so a link the addon
/// points at the server's own network reaches hosts no client could. Link-local addresses (the
/// cloud metadata service at 169.254.169.254 among them) are never fetched; loopback and private
/// ones only with <see cref="PluginConfiguration.AllowLocalNetworkStreams"/> or on the host of a
/// configured addon, whose own proxy serves its streams.
/// </summary>
public static class StreamHosts
{
    /// <summary>The named HttpClient that fetches stream links and checks every address it connects to.</summary>
    public const string ClientName = "GelatoStreams";

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Why the server must not fetch from <paramref name="addresses"/> of <paramref name="host"/>,
    /// or null when it may.
    /// </summary>
    public static string? Blocked(string host, IEnumerable<IPAddress> addresses)
    {
        var local = false;
        foreach (var raw in addresses)
        {
            var address = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (IsLinkLocal(address))
                return "a link-local address, which is never fetched";
            local |= IsLocal(address);
        }

        var cfg = GelatoPlugin.Instance!.Configuration;
        return local && !cfg.AllowLocalNetworkStreams && !IsAddonHost(cfg, host)
            ? "a local network address, and \"Allow streams from the local network\" is off"
            : null;
    }

    /// <summary>
    /// Why the server must not fetch <paramref name="uri"/>, or null when it may or its host
    /// does not resolve (the connection is checked again, see <see cref="ConnectAsync"/>).
    /// </summary>
    public static async Task<string?> BlockedAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.IdnHost;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return Blocked(host, [literal]);

        // Only a link-local address would stop it, and an addon is not served from one.
        var cfg = GelatoPlugin.Instance!.Configuration;
        if (cfg.AllowLocalNetworkStreams && IsAddonHost(cfg, host))
            return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ResolveTimeout);
        try
        {
            return Blocked(host, await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> of <see cref="ClientName"/>: checks the
    /// addresses a host resolves to at the moment of the connection, so a redirect or a name that
    /// resolves elsewhere since the stream sync is held to the same rule.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endPoint.Host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        if (Blocked(endPoint.Host, addresses) is { } reason)
            throw new HttpRequestException($"Stream host {endPoint.Host} is {reason}");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, endPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes() is [169, 254, ..]
            : address.IsIPv6LinkLocal;

    private static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        var b = address.GetAddressBytes();
        return b[0] is 0 or 10
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 172 && b[1] is >= 16 and <= 31);
    }

    // The global addon and every user's own: an AIOStreams on the LAN serves its proxied streams
    // from its own host.
    private static bool IsAddonHost(PluginConfiguration cfg, string host) =>
        cfg.UserConfigs.Select(u => u.Url)
            .Append(cfg.Url)
            .Any(url =>
                Uri.TryCreate(url, UriKind.Absolute, out var addon)
                && string.Equals(addon.IdnHost, host, StringComparison.OrdinalIgnoreCase)
            );
}
