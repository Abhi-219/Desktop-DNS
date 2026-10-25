using System.Net;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class FirewallController
{
    private const string RulePrefix = "FamilyDnsLock_";
    private static readonly string[] WellKnownPublicDnsIpv4 =
    [
        "8.8.8.8", "8.8.4.4",         // Google
        "1.1.1.1", "1.0.0.1",         // Cloudflare
        "9.9.9.9", "149.112.112.112", // Quad9
        "208.67.222.222", "208.67.220.220", // OpenDNS
        "94.140.14.14", "94.140.15.14",     // AdGuard
        "76.76.2.0", "76.76.10.0",           // ControlD
        "4.2.2.1", "4.2.2.2"                 // Level3
    ];

    private static readonly string[] WellKnownPublicDnsIpv6 =
    [
        "2001:4860:4860::8888", "2001:4860:4860::8844", // Google
        "2606:4700:4700::1111", "2606:4700:4700::1001", // Cloudflare
        "2620:fe::fe", "2620:fe::9",                     // Quad9
        "2620:119:35::35", "2620:119:53::53",           // OpenDNS
        "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff"        // AdGuard
    ];

    public async Task ApplyProtectionAsync(IReadOnlyList<IPAddress> allowedServers, CancellationToken cancellationToken)
    {
        // Remove existing rules first to prevent duplicate rules
        await RemoveProtectionAsync(cancellationToken).ConfigureAwait(false);

        var allowedSet = allowedServers.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter out any well-known public DNS IPs that happen to be the chosen provider
        var blockedV4 = WellKnownPublicDnsIpv4.Where(ip => !allowedSet.Contains(ip)).ToArray();
        var blockedV6 = WellKnownPublicDnsIpv6.Where(ip => !allowedSet.Contains(ip)).ToArray();
        var allBlocked = blockedV4.Concat(blockedV6).ToArray();

        var blockedListStr = string.Join(",", allBlocked.Select(ip => $"'{ip}'"));

        var script = $@"
# Block DNS over TLS (Port 853) outbound
New-NetFirewallRule -Name '{RulePrefix}Block_DoT' -DisplayName 'Family DNS Lock - Block DoT (Port 853)' -Direction Outbound -Action Block -Protocol TCP -RemotePort 853 -ErrorAction SilentlyContinue | Out-Null

# Block Direct UDP DNS to unapproved public resolvers
if (@({blockedListStr}).Count -gt 0) {{
    New-NetFirewallRule -Name '{RulePrefix}Block_Unapproved_UDP53' -DisplayName 'Family DNS Lock - Block Unapproved DNS (UDP 53)' -Direction Outbound -Action Block -Protocol UDP -RemotePort 53 -RemoteAddress @({blockedListStr}) -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -Name '{RulePrefix}Block_Unapproved_TCP53' -DisplayName 'Family DNS Lock - Block Unapproved DNS (TCP 53)' -Direction Outbound -Action Block -Protocol TCP -RemotePort 53 -RemoteAddress @({blockedListStr}) -ErrorAction SilentlyContinue | Out-Null
}}
";
        await PowerShellDnsController.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveProtectionAsync(CancellationToken cancellationToken)
    {
        const string script = $@"
Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{RulePrefix}*' }} | Remove-NetFirewallRule -ErrorAction SilentlyContinue
";
        await PowerShellDnsController.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsActiveAsync(CancellationToken cancellationToken)
    {
        const string script = $@"
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{RulePrefix}*' -and $_.Enabled -eq 'True' }}
if ($rules) {{ 'True' }} else {{ 'False' }}
";
        var result = await PowerShellDnsController.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return bool.TryParse(result, out var active) && active;
    }

    public async Task<IReadOnlyList<string>> GetActiveRulesAsync(CancellationToken cancellationToken)
    {
        const string script = $@"
$rules = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{RulePrefix}*' -and $_.Enabled -eq 'True' }}
if (-not $rules) {{ '[]'; exit }}
$rules | Select-Object -ExpandProperty DisplayName | ConvertTo-Json -Compress
";
        var json = await PowerShellDnsController.RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null" || json.Trim() == "[]")
        {
            return [];
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray()
                : [doc.RootElement.GetString() ?? ""];
        }
        catch
        {
            return [];
        }
    }
}
