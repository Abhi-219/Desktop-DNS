namespace FamilyDnsLock.Core;

public enum IpcCommandType
{
    GetStatus,
    Activate,
    EarlyUnlock,
    SetPin,
    VerifyPin,
    RecoverPin,
    UpdateProvider,
    GetDiagnostics,
    AuthorizeUninstall
}

public sealed record IpcRequest(
    IpcCommandType Command,
    string? Pin = null,
    string? NewPin = null,
    string? RecoveryKey = null,
    int? DurationMinutes = null,
    int? DurationDays = null,
    DateTimeOffset? CustomExpiresUtc = null,
    ExpiryAction? ExpiryAction = null,
    string? ProviderName = null,
    IReadOnlyList<string>? ProviderServers = null);

public sealed record IpcResponse(
    bool Success,
    string Message,
    StatusDto? Status = null,
    DiagnosticsDto? Diagnostics = null,
    string? GeneratedRecoveryKey = null,
    TimeSpan? LockoutRemaining = null);

public sealed record StatusDto(
    ProtectionStatus Status,
    bool IsActive,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? ExpiresUtc,
    TimeSpan? Remaining,
    ExpiryAction? ExpiryAction,
    string ProviderName,
    IReadOnlyList<string> ProviderServers,
    bool IsPinConfigured,
    bool IsLockedOut,
    TimeSpan? LockoutRemaining,
    bool FirewallEnforced,
    bool BrowserPolicyEnforced,
    bool UninstallAuthorized);

public sealed record AdapterDiagnostic(
    int InterfaceIndex,
    string Name,
    string Status,
    IReadOnlyList<string> CurrentDns,
    bool HasIpv4,
    bool HasIpv6,
    bool DnsMatchesConfigured);

public sealed record DiagnosticsDto(
    IReadOnlyList<AdapterDiagnostic> Adapters,
    bool FirewallActive,
    IReadOnlyList<string> ActiveFirewallRules,
    bool BrowserPoliciesActive,
    IReadOnlyDictionary<string, string> BrowserPolicies,
    string ServiceLogTail);
