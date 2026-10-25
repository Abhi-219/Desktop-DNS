using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class EnforcementWorker
{
    private readonly PowerShellDnsController _dnsController = new();
    private readonly FirewallController _firewallController = new();
    private readonly BrowserPolicyController _browserController = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyDnsLock",
        "state.json");
    private readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FamilyDnsLock",
        "service.log");

    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly SemaphoreSlim _scheduleChanged = new(0, 1);
    private readonly object _scheduleLock = new();
    private CancellationTokenSource? _workerCts;
    private DateTimeOffset? _aggressiveEnforcementUntilUtc;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ProtectedStorage.EnsureDirectory(Path.GetDirectoryName(_statePath)!);
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Subscribe to network changes for instant reaction
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        var ipcServer = new IpcServer(this);
        var ipcTask = Task.Run(() => ipcServer.ListenAsync(_workerCts.Token), _workerCts.Token);

        WriteLog("Family DNS Lock background enforcement service started.");

        try
        {
            while (!_workerCts.Token.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(_workerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_workerCts.Token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    WriteLog($"Enforcement cycle error: {exception.Message}");
                }

                try
                {
                    await _scheduleChanged.WaitAsync(GetCheckInterval(), _workerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_workerCts.Token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            try
            {
                await ipcTask.ConfigureAwait(false);
            }
            catch { }
            WriteLog("Family DNS Lock service stopped.");
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            WriteLog("Network address change detected. Triggering immediate DNS check.");
            await Task.Delay(1500); // Allow adapter stack to settle
            if (_workerCts is { IsCancellationRequested: false } cts)
            {
                try
                {
                    await ReconcileAsync(cts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        });
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            WriteLog($"Network availability changed (IsAvailable={e.IsAvailable}). Triggering DNS check.");
            await Task.Delay(1500);
            if (_workerCts is { IsCancellationRequested: false } cts)
            {
                try
                {
                    await ReconcileAsync(cts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        });
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!await _reconcileLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Another reconciliation is already running
            return;
        }

        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null || state.Status != ProtectionStatus.Active)
            {
                lock (_scheduleLock)
                {
                    _aggressiveEnforcementUntilUtc = null;
                }
                return;
            }

            var now = DateTimeOffset.UtcNow;
            RestoreAggressivePeriod(state.AggressiveEnforcementUntilUtc, now);
            if (state.ExpiresUtc is not { } expiresUtc || state.ExpiryAction is null)
            {
                WriteLog("Active state is corrupted; enforcement paused.");
                return;
            }

            if (ClockChangeDetector.MovedBackwards(state.StartedUtc ?? now, now))
            {
                WriteLog("System clock moved backwards relative to activation time; enforcement continues until true expiry.");
            }

            // Expiry check
            if (now >= expiresUtc)
            {
                if (state.ExpiryAction == ExpiryAction.RestorePreviousDns)
                {
                    await _dnsController.RestoreAsync(state.PreviousDns, cancellationToken).ConfigureAwait(false);
                }

                await _firewallController.RemoveProtectionAsync(cancellationToken).ConfigureAwait(false);
                _browserController.RemoveBrowserPolicies();

                var expiredState = state.Expire();
                await WriteStateAsync(expiredState, cancellationToken).ConfigureAwait(false);
                lock (_scheduleLock)
                {
                    _aggressiveEnforcementUntilUtc = null;
                }
                WriteLog($"Timed lock expired automatically. Expiry action executed: {state.ExpiryAction}.");
                return;
            }

            // Enforce DNS on all active physical adapters
            var provider = DnsProvider.Create(state.ProviderName, state.ProviderServers);
            var adapters = await _dnsController.GetActivePhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);
            foreach (var adapter in adapters)
            {
                var observed = await _dnsController.ReadAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
                if (!DnsMatches(observed, provider))
                {
                    state = await ActivateAggressiveModeAsync(
                        state,
                        now,
                        $"DNS mismatch detected on '{adapter.Name}'.").ConfigureAwait(false);
                    await _dnsController.ApplyAsync(adapter.InterfaceIndex, provider, cancellationToken).ConfigureAwait(false);
                }
            }

            // Enforce Firewall rules
            if (!await _firewallController.IsActiveAsync(cancellationToken).ConfigureAwait(false))
            {
                state = await ActivateAggressiveModeAsync(
                    state,
                    now,
                    "Firewall protection missing or disabled.").ConfigureAwait(false);
                await _firewallController.ApplyProtectionAsync(provider.Servers, cancellationToken).ConfigureAwait(false);
            }

            // Enforce Browser policies
            if (!_browserController.ArePoliciesApplied())
            {
                state = await ActivateAggressiveModeAsync(
                    state,
                    now,
                    "Browser Secure DNS policies missing.").ConfigureAwait(false);
                _browserController.ApplyBrowserPolicies();
            }
        }

        finally
        {
            _reconcileLock.Release();
        }
    }

    private TimeSpan GetCheckInterval()
    {
        lock (_scheduleLock)
        {
            return EnforcementSchedule.GetCheckInterval(_aggressiveEnforcementUntilUtc, DateTimeOffset.UtcNow);
        }
    }

    private void RestoreAggressivePeriod(DateTimeOffset? storedUntilUtc, DateTimeOffset now)
    {
        if (storedUntilUtc is not { } storedUntil || now >= storedUntil)
        {
            return;
        }

        lock (_scheduleLock)
        {
            if (_aggressiveEnforcementUntilUtc is not { } current || storedUntil > current)
            {
                _aggressiveEnforcementUntilUtc = storedUntil.ToUniversalTime();
            }
        }
    }

    private async Task<ProtectionState> ActivateAggressiveModeAsync(
        ProtectionState state,
        DateTimeOffset detectedAt,
        string reason)
    {
        DateTimeOffset? currentUntil;
        lock (_scheduleLock)
        {
            currentUntil = _aggressiveEnforcementUntilUtc;
        }

        if (state.AggressiveEnforcementUntilUtc is { } storedUntil &&
            (currentUntil is null || storedUntil > currentUntil))
        {
            currentUntil = storedUntil;
        }

        var aggressiveUntil = EnforcementSchedule.StartAggressivePeriod(currentUntil, detectedAt);
        var isNewPeriod = currentUntil is not { } current || detectedAt >= current;
        lock (_scheduleLock)
        {
            _aggressiveEnforcementUntilUtc = aggressiveUntil;
        }

        if (!isNewPeriod)
        {
            return state;
        }

        try
        {
            _scheduleChanged.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        var updatedState = state with { AggressiveEnforcementUntilUtc = aggressiveUntil };
        try
        {
            await WriteStateAsync(updatedState, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WriteLog($"Could not persist aggressive enforcement deadline: {exception.Message}");
        }

        WriteLog($"{reason} Enabling 1-second enforcement checks for 45 minutes, until {aggressiveUntil:O}.");
        return updatedState;
    }

    private static bool DnsMatches(IReadOnlyList<string> actual, DnsProvider expected)
    {
        var actualAddresses = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Servers.All(server => actualAddresses.Contains(server.ToString()));
    }

    public async Task<ProtectionState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_statePath);
            return await JsonSerializer.DeserializeAsync<ProtectionState>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteLog($"Failed to read state file: {ex.Message}");
            return null;
        }
    }

    public async Task WriteStateAsync(ProtectionState state, CancellationToken cancellationToken)
    {
        ProtectedStorage.EnsureDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + ".new";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, state, new JsonSerializerOptions { WriteIndented = true }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, _statePath, overwrite: true);
    }

    public void WriteLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            Trace.WriteLine(message);
        }
    }

    public string GetLogTail(int lines = 30)
    {
        try
        {
            if (!File.Exists(_logPath))
            {
                return "No service log entries yet.";
            }

            var all = File.ReadAllLines(_logPath);
            var tail = all.Skip(Math.Max(0, all.Length - lines));
            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"Error reading logs: {ex.Message}";
        }
    }
}
