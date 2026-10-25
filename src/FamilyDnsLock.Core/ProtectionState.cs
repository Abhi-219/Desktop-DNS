namespace FamilyDnsLock.Core;

public enum ExpiryAction
{
    KeepFilteredDns,
    RestorePreviousDns
}

public enum ProtectionStatus
{
    Inactive,
    Active,
    ExpiredFilteredDnsRetained,
    ExpiredDnsRestored
}

public sealed record AdapterDnsSnapshot(
    int InterfaceIndex,
    string InterfaceName,
    IReadOnlyList<string> Ipv4Servers,
    IReadOnlyList<string> Ipv6Servers);

public sealed record ProtectionState(
    ProtectionStatus Status,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ExpiresUtc,
    ExpiryAction? ExpiryAction,
    string ProviderName,
    IReadOnlyList<string> ProviderServers,
    IReadOnlyList<AdapterDnsSnapshot> PreviousDns)
{
    public static ProtectionState Empty { get; } = new(
        ProtectionStatus.Inactive,
        null,
        null,
        null,
        DnsProvider.CleanBrowsingFamily.Name,
        DnsProvider.CleanBrowsingFamily.Servers.Select(server => server.ToString()).ToArray(),
        []);

    public bool IsActiveAt(DateTimeOffset now) =>
        Status == ProtectionStatus.Active && ExpiresUtc is { } expires && now < expires;

    public ProtectionState Expire() => ExpiryAction switch
    {
        ExpiryAction.KeepFilteredDns => this with { Status = ProtectionStatus.ExpiredFilteredDnsRetained },
        ExpiryAction.RestorePreviousDns => this with { Status = ProtectionStatus.ExpiredDnsRestored },
        _ => throw new InvalidOperationException("An active lock must have an expiry action.")
    };
}

public static class ProtectionStateFactory
{
    public static ProtectionState Activate(
        DateTimeOffset now,
        DateTimeOffset expiresUtc,
        ExpiryAction expiryAction,
        DnsProvider provider,
        IReadOnlyList<AdapterDnsSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(snapshots);

        var startedUtc = now.ToUniversalTime();
        var normalizedExpiry = expiresUtc.ToUniversalTime();
        if (normalizedExpiry <= startedUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "Expiry must be later than activation.");
        }

        return new ProtectionState(
            ProtectionStatus.Active,
            startedUtc,
            normalizedExpiry,
            expiryAction,
            provider.Name,
            provider.Servers.Select(server => server.ToString()).ToArray(),
            snapshots.ToArray());
    }
}

public static class ClockChangeDetector
{
    public static bool MovedBackwards(DateTimeOffset previousUtc, DateTimeOffset currentUtc) =>
        currentUtc.ToUniversalTime() < previousUtc.ToUniversalTime();
}