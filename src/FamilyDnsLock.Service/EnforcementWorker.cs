using System.Diagnostics;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class EnforcementWorker
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private readonly PowerShellDnsController _dnsController = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyDnsLock",
        "state.json");
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyDnsLock",
        "service.log");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ProtectedStorage.EnsureDirectory(Path.GetDirectoryName(_statePath)!);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                WriteLog($"Enforcement check failed: {exception.Message}");
            }

            try
            {
                await Task.Delay(CheckInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.Status != ProtectionStatus.Active)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (state.ExpiresUtc is not { } expiresUtc || state.ExpiryAction is null)
        {
            WriteLog("Active state is invalid; enforcement is paused and requires parent repair.");
            return;
        }

        if (ClockChangeDetector.MovedBackwards(state.StartedUtc ?? now, now))
        {
            WriteLog("System clock is earlier than the recorded activation time; manual review is required.");
            return;
        }

        if (now >= expiresUtc)
        {
            if (state.ExpiryAction == ExpiryAction.RestorePreviousDns)
            {
                await _dnsController.RestoreAsync(state.PreviousDns, cancellationToken).ConfigureAwait(false);
            }

            await WriteStateAsync(state.Expire(), cancellationToken).ConfigureAwait(false);
            WriteLog($"Protection expired; action={state.ExpiryAction}.");
            return;
        }

        var provider = DnsProvider.Create(state.ProviderName, state.ProviderServers);
        var adapters = await _dnsController.GetActivePhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var adapter in adapters)
        {
            await _dnsController.ApplyAsync(adapter.InterfaceIndex, provider, cancellationToken).ConfigureAwait(false);
            var observed = await _dnsController.ReadAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            if (!DnsMatches(observed, provider))
            {
                throw new InvalidOperationException($"DNS verification failed for adapter '{adapter.Name}'.");
            }
        }
    }

    private static bool DnsMatches(IReadOnlyList<string> actual, DnsProvider expected)
    {
        var actualAddresses = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Servers.All(server => actualAddresses.Contains(server.ToString()));
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
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + ".new";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    private void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            Trace.WriteLine(message);
        }
    }
}