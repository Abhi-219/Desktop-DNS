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
    IReadOnlyList<string> Ipv6Servers,
    bool Ipv4IsDhcp = false,
    bool Ipv6IsDhcp = false);

public sealed record ProtectionState(
    ProtectionStatus Status,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ExpiresUtc,
    ExpiryAction? ExpiryAction,
    string ProviderName,
    IReadOnlyList<string> ProviderServers,
    IReadOnlyList<AdapterDnsSnapshot> PreviousDns,
    string? PinHash = null,
    string? PinSalt = null,
    string? RecoveryKeyHash = null,
    string? RecoveryKeySalt = null,
    int FailedPinAttempts = 0,
    DateTimeOffset? LockoutUntilUtc = null,
    DateTimeOffset? UninstallAuthorizedUntilUtc = null,
    DateTimeOffset? AggressiveEnforcementUntilUtc = null)
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

    public bool IsPinConfigured => !string.IsNullOrEmpty(PinHash) && !string.IsNullOrEmpty(PinSalt);

    public bool IsLockedOutAt(DateTimeOffset now) =>
        LockoutUntilUtc is { } lockout && now < lockout;

    public TimeSpan? GetLockoutRemaining(DateTimeOffset now)
    {
        if (LockoutUntilUtc is { } lockout && now < lockout)
        {
            return lockout - now;
        }
        return null;
    }

    public bool IsUninstallAuthorizedAt(DateTimeOffset now) =>
        UninstallAuthorizedUntilUtc is { } authUntil && now < authUntil;

    public ProtectionState Expire() => ExpiryAction switch
    {
        global::FamilyDnsLock.Core.ExpiryAction.KeepFilteredDns => this with
        {
            Status = ProtectionStatus.ExpiredFilteredDnsRetained,
            AggressiveEnforcementUntilUtc = null
        },
        global::FamilyDnsLock.Core.ExpiryAction.RestorePreviousDns => this with
        {
            Status = ProtectionStatus.ExpiredDnsRestored,
            AggressiveEnforcementUntilUtc = null
        },
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
        IReadOnlyList<AdapterDnsSnapshot> snapshots,
        string? pinHash = null,
        string? pinSalt = null,
        string? recoveryKeyHash = null,
        string? recoveryKeySalt = null)
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
            snapshots.ToArray(),
            pinHash,
            pinSalt,
            recoveryKeyHash,
            recoveryKeySalt);
    }
}

public static class ClockChangeDetector
{
    public static bool MovedBackwards(DateTimeOffset previousUtc, DateTimeOffset currentUtc) =>
        currentUtc.ToUniversalTime() < previousUtc.ToUniversalTime();
}
