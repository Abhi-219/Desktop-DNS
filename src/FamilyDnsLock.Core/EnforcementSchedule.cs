namespace FamilyDnsLock.Core;

public static class EnforcementSchedule
{
    public static readonly TimeSpan NormalCheckInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AggressiveCheckInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan AggressiveDuration = TimeSpan.FromMinutes(45);

    public static TimeSpan GetCheckInterval(DateTimeOffset? aggressiveUntilUtc, DateTimeOffset now)
    {
        return aggressiveUntilUtc is { } until && now.ToUniversalTime() < until.ToUniversalTime()
            ? AggressiveCheckInterval
            : NormalCheckInterval;
    }

    public static DateTimeOffset StartAggressivePeriod(DateTimeOffset? currentUntilUtc, DateTimeOffset detectedAt)
    {
        var nowUtc = detectedAt.ToUniversalTime();
        if (currentUntilUtc is { } current && nowUtc < current.ToUniversalTime())
        {
            return current.ToUniversalTime();
        }

        return nowUtc.Add(AggressiveDuration);
    }
}
