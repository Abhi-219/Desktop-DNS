using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class PowerShellDnsController
{
    public async Task<IReadOnlyList<NetworkAdapter>> GetActivePhysicalAdaptersAsync(CancellationToken cancellationToken)
    {
        const string script = @"
$adapters = Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' }
if (-not $adapters) { '[]'; exit }
$result = foreach ($a in $adapters) {
    [PSCustomObject]@{
        InterfaceIndex = [int]$a.ifIndex
        Name = [string]$a.Name
    }
}
$result | ConvertTo-Json -Compress
";
        var json = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null" || json.Trim() == "[]")
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];

        return rows.Select(row => new NetworkAdapter(
                row.GetProperty("InterfaceIndex").GetInt32(),
                row.GetProperty("Name").GetString() ?? "Unknown adapter"))
            .ToArray();
    }

    public async Task<AdapterDnsSnapshot> CaptureSnapshotAsync(int interfaceIndex, string adapterName, CancellationToken cancellationToken)
    {
        var script = $@"
$idx = {interfaceIndex}
$dns = Get-DnsClientServerAddress -InterfaceIndex $idx -ErrorAction SilentlyContinue
$v4Dns = @($dns | Where-Object {{ $_.AddressFamily -eq 2 }} | ForEach-Object {{ $_.ServerAddresses }})
$v6Dns = @($dns | Where-Object {{ $_.AddressFamily -eq 23 }} | ForEach-Object {{ $_.ServerAddresses }})

$v4If = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue
$v6If = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv6 -ErrorAction SilentlyContinue

$v4Dhcp = if ($v4If -and $v4If.Dhcp -eq 'Enabled') {{ $true }} else {{ $false }}
$v6Dhcp = if ($v6If -and ($v6If.Dhcp -eq 'Enabled' -or $v6If.RouterDiscovery -eq 'Enabled')) {{ $true }} else {{ $false }}

[PSCustomObject]@{{
    InterfaceIndex = $idx
    InterfaceName = '{adapterName}'
    Ipv4Servers = @($v4Dns)
    Ipv6Servers = @($v6Dns)
    Ipv4IsDhcp = $v4Dhcp
    Ipv6IsDhcp = $v6Dhcp
}} | ConvertTo-Json -Compress
";
        var json = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AdapterDnsSnapshot(interfaceIndex, adapterName, [], [], true, true);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var v4Servers = root.GetProperty("Ipv4Servers").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var v6Servers = root.GetProperty("Ipv6Servers").EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var v4Dhcp = root.GetProperty("Ipv4IsDhcp").GetBoolean();
        var v6Dhcp = root.GetProperty("Ipv6IsDhcp").GetBoolean();

        return new AdapterDnsSnapshot(interfaceIndex, adapterName, v4Servers, v6Servers, v4Dhcp, v6Dhcp);
    }

    public async Task ApplyAsync(int interfaceIndex, DnsProvider provider, CancellationToken cancellationToken)
    {
        if (interfaceIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        }

        var addresses = string.Join(",", provider.Servers.Select(address => $"'{address}'"));
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses @({addresses}) -ErrorAction Stop";
        await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadAsync(int interfaceIndex, CancellationToken cancellationToken)
    {
        if (interfaceIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        }

        var script = $"Get-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ErrorAction SilentlyContinue | " +
            "ForEach-Object { $_.ServerAddresses } | ConvertTo-Json -Compress";
        var json = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s)).ToArray()
            : [document.RootElement.GetString() ?? string.Empty];
    }

    public async Task RestoreAsync(
        IReadOnlyList<AdapterDnsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            // If IPv4 was DHCP, reset server addresses to DHCP instead of setting static IPs
            if (snapshot.Ipv4IsDhcp && snapshot.Ipv6IsDhcp)
            {
                var resetScript = $"Set-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex} -ResetServerAddresses -ErrorAction SilentlyContinue";
                await RunPowerShellAsync(resetScript, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var servers = snapshot.Ipv4Servers.Concat(snapshot.Ipv6Servers)
                .Where(s => IPAddress.TryParse(s, out _))
                .Select(IPAddress.Parse)
                .Distinct()
                .ToArray();

            if (servers.Length == 0)
            {
                var resetScript = $"Set-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex} -ResetServerAddresses -ErrorAction SilentlyContinue";
                await RunPowerShellAsync(resetScript, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var addresses = string.Join(",", servers.Select(address => $"'{address}'"));
                var script = $"Set-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex} -ServerAddresses @({addresses}) -ErrorAction SilentlyContinue";
                await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"PowerShell exited with code {process.ExitCode}."
                : error.Trim());
        }

        return output.Trim();
    }

    internal sealed record NetworkAdapter(int InterfaceIndex, string Name);
}
