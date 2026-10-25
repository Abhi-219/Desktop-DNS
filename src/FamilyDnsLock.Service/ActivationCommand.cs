using System.Net;
using System.Security.Principal;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class ActivationCommand
{
    private readonly PowerShellDnsController _dnsController = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyDnsLock",
        "state.json");

    public async Task ActivateAsync(TimeSpan duration, ExpiryAction expiryAction, CancellationToken cancellationToken)
    {
        if (duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromDays(90))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be between 1 minute and 90 days.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protection can only be activated on Windows.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException("Run activation from the parent-controlled administrator account.");
        }

        var storageDirectory = Path.GetDirectoryName(_statePath)!;
        ProtectedStorage.EnsureDirectory(storageDirectory);
        var previousState = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (previousState is { Status: ProtectionStatus.Active } activeState &&
            activeState.IsActiveAt(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("A protection period is already active. Use the authorized unlock workflow first.");
        }

        var adapters = await _dnsController.GetActivePhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);
        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("No active physical Ethernet or Wi-Fi adapters were found.");
        }

        var snapshots = new List<AdapterDnsSnapshot>();
        foreach (var adapter in adapters)
        {
            var current = await _dnsController.ReadAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            var addresses = current.Where(value => IPAddress.TryParse(value, out _))
                .Select(IPAddress.Parse)
                .ToArray();
            snapshots.Add(new AdapterDnsSnapshot(
                adapter.InterfaceIndex,
                adapter.Name,
                addresses.Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(address => address.ToString()).ToArray(),
                addresses.Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .Select(address => address.ToString()).ToArray()));
        }

        var now = DateTimeOffset.UtcNow;
        var state = ProtectionStateFactory.Activate(
            now,
            now.Add(duration),
            expiryAction,
            DnsProvider.CleanBrowsingFamily,
            snapshots);

        await WriteStateAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var adapter in adapters)
            {
                await _dnsController.ApplyAsync(adapter.InterfaceIndex, DnsProvider.CleanBrowsingFamily, cancellationToken)
                    .ConfigureAwait(false);
                var observed = await _dnsController.ReadAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
                var observedSet = observed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!DnsProvider.CleanBrowsingFamily.Servers.All(server => observedSet.Contains(server.ToString())))
                {
                    throw new InvalidOperationException($"DNS verification failed for adapter '{adapter.Name}'.");
                }
            }
        }
        catch (Exception activationError)
        {
            try
            {
                await _dnsController.RestoreAsync(snapshots, CancellationToken.None).ConfigureAwait(false);
                await WriteStateAsync(previousState ?? ProtectionState.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Activation failed and DNS rollback also failed; the active state was retained for service recovery.",
                    activationError,
                    rollbackError);
            }

            throw;
        }

        Console.WriteLine($"DNS readback verified on {adapters.Count} active adapter(s).");
        Console.WriteLine($"Protection recorded for {duration}; expires at {state.ExpiresUtc:O}; expiry action: {expiryAction}.");
        Console.WriteLine("This command does not install or start the background service.");
    }

    private async Task<ProtectionState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_statePath);
        return await JsonSerializer.DeserializeAsync<ProtectionState>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteStateAsync(ProtectionState state, CancellationToken cancellationToken)
    {
        var temporaryPath = _statePath + ".new";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, _statePath, overwrite: true);
    }
}