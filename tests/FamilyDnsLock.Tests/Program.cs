using System.Net;
using System.Text.Json;
using FamilyDnsLock.Core;

Console.WriteLine("=================================================");
Console.WriteLine("Family DNS Lock - Comprehensive Automated Tests");
Console.WriteLine("=================================================");

var checks = new (string Name, Action Run)[]
{
    ("Default Provider has documented IPv4 and IPv6 addresses", VerifyDefaultProvider),
    ("Custom Provider rejects configurations missing IPv4 or IPv6", RejectIncompleteProvider),
    ("Custom Provider accepts valid dual-stack configurations", AcceptValidDualStackProvider),
    ("PIN Security: Unique salts generated for same PIN", VerifyUniqueSalts),
    ("PIN Security: PBKDF2 hash verification succeeds with correct PIN", VerifyPinCorrect),
    ("PIN Security: PBKDF2 hash verification fails with wrong PIN", VerifyPinIncorrect),
    ("PIN Security: Rate limiting delays escalate progressively", VerifyRateLimitingDelays),
    ("PIN Security: Recovery key generation and format validation", VerifyRecoveryKeyFormatAndVerification),
    ("Lock Activation: UTC normalization and active state calculation", VerifyActivation),
    ("Lock Activation: Rejects expiry before or equal to start", RejectInvalidExpiry),
    ("Lock Expiry: State transition adheres to ExpiryAction choice", VerifyExpiryActions),
    ("Clock Tampering: Detects backwards time travel", DetectClockRollback),
    ("Tamper response: Checks every second for 45 minutes after drift", VerifyTamperResponseSchedule),
    ("Adapter Snapshot: Preserves DHCP flags for exact restoration", VerifyDhcpFlagPreservation),
    ("IPC Contracts: Serializes and deserializes request and response", VerifyIpcContracts),
    ("Uninstall Guard: Enforces 10-minute temporary authorization window", VerifyUninstallAuthorization)
};

var failed = 0;
foreach (var check in checks)
{
    try
    {
        check.Run();
        Console.WriteLine($"[PASS] {check.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine($"[FAIL] {check.Name}: {exception.Message}");
    }
}

Console.WriteLine("=================================================");
Console.WriteLine($"Results: {checks.Length - failed}/{checks.Length} tests passed.");
Console.WriteLine("=================================================");
return failed == 0 ? 0 : 1;

static void VerifyDefaultProvider()
{
    var provider = DnsProvider.CleanBrowsingFamily;
    Assert(provider.Servers.Count == 4, "Expected four provider addresses.");
    Assert(provider.Servers.Any(a => a.ToString() == "185.228.168.168"), "CleanBrowsing IPv4 primary missing.");
    Assert(provider.Servers.Any(a => a.ToString() == "185.228.169.168"), "CleanBrowsing IPv4 secondary missing.");
    Assert(provider.Servers.Any(a => a.ToString() == "2a0d:2a00:1::"), "CleanBrowsing IPv6 primary missing.");
    Assert(provider.Servers.Any(a => a.ToString() == "2a0d:2a00:2::"), "CleanBrowsing IPv6 secondary missing.");
}

static void RejectIncompleteProvider()
{
    AssertThrows<ArgumentException>(() => DnsProvider.Create("IPv4 only", ["1.1.1.1"]));
    AssertThrows<ArgumentException>(() => DnsProvider.Create("IPv6 only", ["2606:4700:4700::1111"]));
}

static void AcceptValidDualStackProvider()
{
    var custom = DnsProvider.Create("Custom Dual", ["1.1.1.1", "2606:4700:4700::1111"]);
    Assert(custom.Servers.Count == 2, "Expected 2 servers.");
}

static void VerifyUniqueSalts()
{
    var (hash1, salt1) = PinSecurityManager.HashPin("1234");
    var (hash2, salt2) = PinSecurityManager.HashPin("1234");
    Assert(salt1 != salt2, "Salts must be randomly generated and unique.");
    Assert(hash1 != hash2, "Hashes of same PIN with different salts must differ.");
}

static void VerifyPinCorrect()
{
    var (hash, salt) = PinSecurityManager.HashPin("9876");
    Assert(PinSecurityManager.VerifyPin("9876", hash, salt), "Verification failed with matching PIN.");
}

static void VerifyPinIncorrect()
{
    var (hash, salt) = PinSecurityManager.HashPin("9876");
    Assert(!PinSecurityManager.VerifyPin("0000", hash, salt), "Verification succeeded with wrong PIN.");
    Assert(!PinSecurityManager.VerifyPin("", hash, salt), "Empty PIN should fail verification.");
}

static void VerifyRateLimitingDelays()
{
    Assert(PinSecurityManager.GetLockoutDelay(0) == TimeSpan.Zero, "0 attempts should have no delay.");
    Assert(PinSecurityManager.GetLockoutDelay(2) == TimeSpan.Zero, "2 attempts should have no delay.");
    Assert(PinSecurityManager.GetLockoutDelay(3) == TimeSpan.FromSeconds(5), "3 attempts should delay 5s.");
    Assert(PinSecurityManager.GetLockoutDelay(4) == TimeSpan.FromSeconds(30), "4 attempts should delay 30s.");
    Assert(PinSecurityManager.GetLockoutDelay(5) == TimeSpan.FromMinutes(2), "5 attempts should delay 2min.");
    Assert(PinSecurityManager.GetLockoutDelay(10) == TimeSpan.FromMinutes(10), "10 attempts should delay 10min.");
}

static void VerifyRecoveryKeyFormatAndVerification()
{
    var key = PinSecurityManager.GenerateRecoveryKey();
    Assert(key.StartsWith("FDNS-"), "Key must start with FDNS- prefix.");
    Assert(key.Length == 24, $"Expected 24 chars, got {key.Length} ({key}).");

    var (recHash, recSalt) = PinSecurityManager.HashPin(key);
    Assert(PinSecurityManager.VerifyPin(key, recHash, recSalt), "Recovery key verification failed.");
    Assert(!PinSecurityManager.VerifyPin("FDNS-WRONG-KEY-1234-5678", recHash, recSalt), "Wrong recovery key succeeded.");
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

static void DetectClockRollback()
{
    var previous = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    Assert(ClockChangeDetector.MovedBackwards(previous, previous.AddMinutes(-1)), "Clock rollback was not detected.");
    Assert(!ClockChangeDetector.MovedBackwards(previous, previous.AddMinutes(1)), "Normal clock advance was reported as rollback.");
}

static void VerifyTamperResponseSchedule()
{
    var detectedAt = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
    Assert(
        EnforcementSchedule.GetCheckInterval(null, detectedAt) == TimeSpan.FromSeconds(30),
        "Normal enforcement should check every 30 seconds.");

    var aggressiveUntil = EnforcementSchedule.StartAggressivePeriod(null, detectedAt);
    Assert(aggressiveUntil == detectedAt.AddMinutes(45), "Aggressive period should last 45 minutes.");
    Assert(
        EnforcementSchedule.GetCheckInterval(aggressiveUntil, detectedAt.AddMinutes(1)) == TimeSpan.FromSeconds(1),
        "Active aggressive enforcement should check every second.");
    Assert(
        EnforcementSchedule.StartAggressivePeriod(aggressiveUntil, detectedAt.AddMinutes(10)) == aggressiveUntil,
        "A detection during an active aggressive period should not shorten or extend its deadline.");
    Assert(
        EnforcementSchedule.GetCheckInterval(aggressiveUntil, aggressiveUntil) == TimeSpan.FromSeconds(30),
        "Enforcement should return to its normal interval when the aggressive period expires.");
}

static void VerifyDhcpFlagPreservation()
{
    var snapshot = new AdapterDnsSnapshot(
        12,
        "Wi-Fi",
        ["192.168.1.1"],
        ["fe80::1"],
        Ipv4IsDhcp: true,
        Ipv6IsDhcp: true);

    Assert(snapshot.Ipv4IsDhcp, "IPv4 DHCP flag not set.");
    Assert(snapshot.Ipv6IsDhcp, "IPv6 DHCP flag not set.");
    Assert(snapshot.InterfaceIndex == 12, "Interface index mismatch.");
}

static void VerifyIpcContracts()
{
    var req = new IpcRequest(
        IpcCommandType.Activate,
        Pin: "1234",
        DurationDays: 7,
        ExpiryAction: ExpiryAction.KeepFilteredDns);

    var json = JsonSerializer.Serialize(req);
    var deserialized = JsonSerializer.Deserialize<IpcRequest>(json);
    Assert(deserialized is not null, "Deserialization returned null.");
    Assert(deserialized!.Command == IpcCommandType.Activate, "Command mismatch.");
    Assert(deserialized.DurationDays == 7, "Duration mismatch.");
    Assert(deserialized.ExpiryAction == ExpiryAction.KeepFilteredDns, "ExpiryAction mismatch.");
}

static void VerifyUninstallAuthorization()
{
    var now = DateTimeOffset.UtcNow;
    var state = ProtectionState.Empty with
    {
        UninstallAuthorizedUntilUtc = now.AddMinutes(10)
    };

    Assert(state.IsUninstallAuthorizedAt(now.AddMinutes(5)), "Should be authorized within 10 min window.");
    Assert(!state.IsUninstallAuthorizedAt(now.AddMinutes(11)), "Should expire after 10 min window.");
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
