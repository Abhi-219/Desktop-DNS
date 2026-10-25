using System.Net;

namespace FamilyDnsLock.Core;

public sealed record DnsProvider(string Name, IReadOnlyList<IPAddress> Servers)
{
    public static DnsProvider CleanBrowsingFamily { get; } = new(
        "CleanBrowsing Family Filter",
        [
            IPAddress.Parse("185.228.168.168"),
            IPAddress.Parse("185.228.169.168"),
            IPAddress.Parse("2a0d:2a00:1::"),
            IPAddress.Parse("2a0d:2a00:2::")
        ]);

    public static DnsProvider Create(string name, IEnumerable<string> addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(addresses);

        var parsed = addresses.Select(IPAddress.Parse).Distinct().ToArray();
        if (parsed.Length == 0)
        {
            throw new ArgumentException("At least one DNS server address is required.", nameof(addresses));
        }

        if (!parsed.Any(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ||
            !parsed.Any(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("Configure at least one IPv4 and one IPv6 DNS address.", nameof(addresses));
        }

        return new DnsProvider(name.Trim(), parsed);
    }
}