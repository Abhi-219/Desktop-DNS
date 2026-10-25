using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class PowerShellDnsController
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<NetworkAdapter>> GetActivePhysicalAdaptersAsync(CancellationToken cancellationToken)
    {
        const string script = "Get-NetAdapter -Physical | Where-Object { $_.Status -eq 'Up' } | " +
            "Select-Object @{Name='InterfaceIndex';Expression={$_.ifIndex}},Name | ConvertTo-Json -Compress";
        var json = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
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

    public async Task ApplyAsync(int interfaceIndex, DnsProvider provider, CancellationToken cancellationToken)
    {
        if (interfaceIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        }

        var addresses = string.Join(",", provider.Servers.Select(address => $"'{IPAddress.Parse(address.ToString())}'"));
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} " +
            $"-ServerAddresses @({addresses}) -ErrorAction Stop";
        await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadAsync(int interfaceIndex, CancellationToken cancellationToken)
    {
        if (interfaceIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(interfaceIndex));
        }

        var script = $"Get-DnsClientServerAddress -InterfaceIndex {interfaceIndex} | " +
            "ForEach-Object { $_.ServerAddresses } | ConvertTo-Json -Compress";
        var json = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray()
            : [document.RootElement.GetString() ?? string.Empty];
    }

    public async Task RestoreAsync(
        IReadOnlyList<AdapterDnsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            var servers = snapshot.Ipv4Servers.Concat(snapshot.Ipv6Servers)
                .Select(IPAddress.Parse)
                .Distinct()
                .ToArray();
            if (servers.Length == 0)
            {
                var resetScript = $"Set-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex} " +
                    "-ResetServerAddresses -ErrorAction Stop";
                await RunPowerShellAsync(resetScript, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var addresses = string.Join(",", servers.Select(address => $"'{IPAddress.Parse(address)}'"));
            var script = $"Set-DnsClientServerAddress -InterfaceIndex {snapshot.InterfaceIndex} " +
                $"-ServerAddresses @({addresses}) -ErrorAction Stop";
            await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
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