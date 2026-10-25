using FamilyDnsLock.Core;

var checks = new (string Name, Action Run)[]
{
    ("CleanBrowsing includes IPv4 and IPv6", VerifyDefaultProvider),
    ("Provider rejects configurations missing an address family", RejectIncompleteProvider),
    ("Activation stores UTC timestamps and the expiry choice", VerifyActivation),
    ("Expiry before activation is rejected", RejectInvalidExpiry),
    ("Clock rollback is detected", DetectClockRollback),
    ("Expiry choice controls the final state", VerifyExpiryActions)
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"FAIL {check.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{checks.Length - failed}/{checks.Length} checks passed.");
return failed == 0 ? 0 : 1;

static void VerifyDefaultProvider()
{
    var provider = DnsProvider.CleanBrowsingFamily;
    Assert(provider.Servers.Count == 4, "Expected four provider addresses.");
    Assert(provider.Servers.Any(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), "IPv4 missing.");
    Assert(provider.Servers.Any(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6), "IPv6 missing.");
}

static void RejectIncompleteProvider()
{
    AssertThrows<ArgumentException>(() => DnsProvider.Create("IPv4 only", ["1.1.1.1"]));
}

static void VerifyActivation()
{
    var start = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(-7));
    var expiry = start.AddDays(7);
    var state = ProtectionStateFactory.Activate(
        start,
        expiry,
        ExpiryAction.KeepFilteredDns,
        DnsProvider.CleanBrowsingFamily,
        []);

    Assert(state.StartedUtc == start.ToUniversalTime(), "Activation timestamp was not normalized to UTC.");
    Assert(state.ExpiresUtc == expiry.ToUniversalTime(), "Expiry timestamp was not normalized to UTC.");
    Assert(state.IsActiveAt(start.ToUniversalTime().AddDays(1)), "Expected state to remain active before expiry.");
    Assert(!state.IsActiveAt(expiry.ToUniversalTime()), "State must not be active at expiry.");
}

static void RejectInvalidExpiry()
{
    var now = DateTimeOffset.UtcNow;
    AssertThrows<ArgumentOutOfRangeException>(() => ProtectionStateFactory.Activate(
        now,
        now,
        ExpiryAction.RestorePreviousDns,
        DnsProvider.CleanBrowsingFamily,
        []));
}

static void DetectClockRollback()
{
    var previous = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    Assert(ClockChangeDetector.MovedBackwards(previous, previous.AddMinutes(-1)), "Clock rollback was not detected.");
    Assert(!ClockChangeDetector.MovedBackwards(previous, previous.AddMinutes(1)), "Normal clock advance was reported as rollback.");
}

static void VerifyExpiryActions()
{
    var state = ProtectionStateFactory.Activate(
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(1),
        ExpiryAction.KeepFilteredDns,
        DnsProvider.CleanBrowsingFamily,
        []);
    Assert(state.Expire().Status == ProtectionStatus.ExpiredFilteredDnsRetained, "Filtered DNS retention choice was ignored.");

    var restoreState = state with { ExpiryAction = ExpiryAction.RestorePreviousDns };
    Assert(restoreState.Expire().Status == ProtectionStatus.ExpiredDnsRestored, "DNS restoration choice was ignored.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}