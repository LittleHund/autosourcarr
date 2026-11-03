using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AutoSourcarr.Core.Settings;

/// <summary>
/// Phase 13 audit fix (AUTH-1, external sign-in): who may send identity and forwarding headers. Only peers listed in
/// AUTH_TRUSTED_PROXIES count; an empty list trusts nobody. Loopback and the container's own interface addresses never
/// count, even when a listed subnet contains them, because the source host JVM (extension code) lives in this container
/// and would otherwise sign in as the owner by sending Remote-User. 0.4.5 used these headers only to pick UI preferences
/// (access was the admin token behind Authelia), so this list is new for owners who sign in through Authelia.
/// </summary>
public static class TrustedProxies
{
    public const string MissingMessage = "External sign-in needs AUTH_TRUSTED_PROXIES set to your reverse proxy's address "
        + "(for example the Authelia or Nginx Proxy Manager container IP, or its Docker network such as 172.18.0.0/16). "
        + "Until then nobody can sign in through the proxy; the admin token and API keys still work.";
    private static readonly Lock Gate = new();
    private static (DateTime At, HashSet<IPAddress> Addresses) _own = (DateTime.MinValue, []);

    /// <summary>The container's own interface addresses (refreshed every minute), plus loopback.</summary>
    public static Func<IReadOnlySet<IPAddress>> OwnAddresses { get; set; } = ReadOwnAddresses;

    public static bool Trusted(IPAddress? remote, string configured)
    {
        if (remote is null) return false; // no network peer to check: never a proxy
        remote = Normalize(remote);
        if (IPAddress.IsLoopback(remote) || OwnAddresses().Contains(remote)) return false;
        foreach (var entry in Entries(configured))
        {
            if (IPNetwork.TryParse(entry, out var network) && network.Contains(remote)) return true;
            if (IPAddress.TryParse(entry, out var single) && Normalize(single).Equals(remote)) return true;
        }
        return false;
    }

    /// <summary>What is wrong with a configured list (empty when it is usable). Loopback and own addresses are refused.</summary>
    public static IReadOnlyList<string> Problems(string configured, bool external)
    {
        var problems = new List<string>();
        var entries = Entries(configured).ToArray();
        if (entries.Length == 0)
        {
            if (external) problems.Add(MissingMessage);
            return problems;
        }
        var own = OwnAddresses();
        foreach (var entry in entries)
        {
            if (IPAddress.TryParse(entry, out var single))
            {
                single = Normalize(single);
                if (IPAddress.IsLoopback(single)) problems.Add("AUTH_TRUSTED_PROXIES must not list loopback (" + entry + "): the source host runs on this machine. List your reverse proxy's address.");
                else if (own.Contains(single)) problems.Add("AUTH_TRUSTED_PROXIES must not list this container's own address (" + entry + "): the source host runs here. List your reverse proxy's address.");
                else if (single.Equals(IPAddress.Any) || single.Equals(IPAddress.IPv6Any)) problems.Add("AUTH_TRUSTED_PROXIES entry " + entry + " is not an address.");
            }
            else if (IPNetwork.TryParse(entry, out var network))
            {
                if (network.Contains(IPAddress.Loopback) || network.Contains(IPAddress.IPv6Loopback))
                    problems.Add("AUTH_TRUSTED_PROXIES must not include loopback (" + entry + "): the source host runs on this machine.");
                else if (network.PrefixLength == 0) problems.Add("AUTH_TRUSTED_PROXIES entry " + entry + " would trust every address.");
            }
            else problems.Add("AUTH_TRUSTED_PROXIES entry " + entry + " is not an IP address or CIDR range.");
        }
        return problems;
    }

    private static IEnumerable<string> Entries(string configured) =>
        (configured ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IReadOnlySet<IPAddress> ReadOwnAddresses()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _own.At < TimeSpan.FromMinutes(1)) return _own.Addresses;
            var addresses = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                        if (unicast.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                            addresses.Add(Normalize(unicast.Address));
            }
            catch (Exception error) when (error is NetworkInformationException or PlatformNotSupportedException) { _ = error; }
            _own = (DateTime.UtcNow, addresses);
            return addresses;
        }
    }
}
