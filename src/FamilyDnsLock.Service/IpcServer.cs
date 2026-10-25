using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal sealed class IpcServer
{
    private const string PipeName = "FamilyDnsLockPipe";
    private readonly EnforcementWorker _worker;
    private readonly PowerShellDnsController _dnsController = new();
    private readonly FirewallController _firewallController = new();
    private readonly BrowserPolicyController _browserController = new();

    public IpcServer(EnforcementWorker worker)
    {
        _worker = worker;
    }

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? serverStream = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipeSecurity = new PipeSecurity();
                    var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    var authenticatedUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

                    pipeSecurity.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
                    pipeSecurity.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));
                    pipeSecurity.AddAccessRule(new PipeAccessRule(authenticatedUsersSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    serverStream = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);
                }
                else
                {
                    serverStream = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                }

                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleConnectionAsync(serverStream, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                serverStream?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                _worker.WriteLog($"IPC server accept error: {ex.Message}");
                serverStream?.Dispose();
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        using (stream)
        {
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                var request = JsonSerializer.Deserialize<IpcRequest>(line);
                if (request is null)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse(false, "Invalid request payload."))).ConfigureAwait(false);
                    return;
                }

                var response = await ProcessRequestAsync(request, cancellationToken).ConfigureAwait(false);
                var responseJson = JsonSerializer.Serialize(response);
                await writer.WriteLineAsync(responseJson).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _worker.WriteLog($"IPC handle error: {ex.Message}");
                try
                {
                    var errorResponse = new IpcResponse(false, $"Server error: {ex.Message}");
                    await writer.WriteLineAsync(JsonSerializer.Serialize(errorResponse)).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task<IpcResponse> ProcessRequestAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var state = await _worker.ReadStateAsync(cancellationToken).ConfigureAwait(false) ?? ProtectionState.Empty;
        var now = DateTimeOffset.UtcNow;

        switch (request.Command)
        {
            case IpcCommandType.GetStatus:
                return BuildStatusResponse(state, now);

            case IpcCommandType.VerifyPin:
                return HandleVerifyPin(state, request.Pin, now);

            case IpcCommandType.SetPin:
                return await HandleSetPinAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.RecoverPin:
                return await HandleRecoverPinAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.UpdateProvider:
                return await HandleUpdateProviderAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.Activate:
                return await HandleActivateAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.EarlyUnlock:
                return await HandleEarlyUnlockAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.GetDiagnostics:
                return await HandleGetDiagnosticsAsync(state, cancellationToken).ConfigureAwait(false);

            case IpcCommandType.AuthorizeUninstall:
                return await HandleAuthorizeUninstallAsync(state, request, now, cancellationToken).ConfigureAwait(false);

            default:
                return new IpcResponse(false, "Unknown command.");
        }
    }

    private IpcResponse BuildStatusResponse(ProtectionState state, DateTimeOffset now)
    {
        var isActive = state.IsActiveAt(now);
        var remaining = isActive && state.ExpiresUtc.HasValue ? state.ExpiresUtc.Value - now : (TimeSpan?)null;
        var lockoutRemaining = state.GetLockoutRemaining(now);

        var statusDto = new StatusDto(
            state.Status,
            isActive,
            state.StartedUtc,
            state.ExpiresUtc,
            remaining,
            state.ExpiryAction,
            state.ProviderName,
            state.ProviderServers,
            state.IsPinConfigured,
            state.IsLockedOutAt(now),
            lockoutRemaining,
            _firewallController.IsActiveAsync(CancellationToken.None).GetAwaiter().GetResult(),
            _browserController.ArePoliciesApplied(),
            state.IsUninstallAuthorizedAt(now));

        return new IpcResponse(true, "Status retrieved.", Status: statusDto);
    }

    private IpcResponse HandleVerifyPin(ProtectionState state, string? pin, DateTimeOffset now)
    {
        if (!state.IsPinConfigured)
        {
            return new IpcResponse(false, "No parent PIN has been configured yet.");
        }

        if (state.IsLockedOutAt(now))
        {
            var remaining = state.GetLockoutRemaining(now);
            return new IpcResponse(false, $"Too many incorrect attempts. Locked out for {remaining?.TotalSeconds:F0} more seconds.", LockoutRemaining: remaining);
        }

        if (string.IsNullOrWhiteSpace(pin) || !PinSecurityManager.VerifyPin(pin, state.PinHash!, state.PinSalt!))
        {
            var failed = state.FailedPinAttempts + 1;
            var delay = PinSecurityManager.GetLockoutDelay(failed);
            var lockoutUntil = delay > TimeSpan.Zero ? now.Add(delay) : (DateTimeOffset?)null;
            var updated = state with { FailedPinAttempts = failed, LockoutUntilUtc = lockoutUntil };
            _worker.WriteStateAsync(updated, CancellationToken.None).GetAwaiter().GetResult();

            return new IpcResponse(false, delay > TimeSpan.Zero
                ? $"Incorrect PIN. Rate limited for {delay.TotalSeconds:F0} seconds."
                : "Incorrect PIN. Please try again.",
                LockoutRemaining: delay > TimeSpan.Zero ? delay : null);
        }

        // Success: reset failed attempts
        if (state.FailedPinAttempts > 0 || state.LockoutUntilUtc.HasValue)
        {
            var resetState = state with { FailedPinAttempts = 0, LockoutUntilUtc = null };
            _worker.WriteStateAsync(resetState, CancellationToken.None).GetAwaiter().GetResult();
        }

        return new IpcResponse(true, "PIN verified successfully.");
    }

    private async Task<IpcResponse> HandleSetPinAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.IsPinConfigured)
        {
            if (state.IsLockedOutAt(now))
            {
                var remaining = state.GetLockoutRemaining(now);
                return new IpcResponse(false, $"Locked out. Try again in {remaining?.TotalSeconds:F0} seconds.", LockoutRemaining: remaining);
            }

            if (string.IsNullOrWhiteSpace(request.Pin) || !PinSecurityManager.VerifyPin(request.Pin, state.PinHash!, state.PinSalt!))
            {
                var failed = state.FailedPinAttempts + 1;
                var delay = PinSecurityManager.GetLockoutDelay(failed);
                var lockoutUntil = delay > TimeSpan.Zero ? now.Add(delay) : (DateTimeOffset?)null;
                await _worker.WriteStateAsync(state with { FailedPinAttempts = failed, LockoutUntilUtc = lockoutUntil }, cancellationToken).ConfigureAwait(false);
                return new IpcResponse(false, "Incorrect existing PIN.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.NewPin) || request.NewPin.Length < 4 || request.NewPin.Length > 16)
        {
            return new IpcResponse(false, "New PIN must be between 4 and 16 digits.");
        }

        var (pinHash, pinSalt) = PinSecurityManager.HashPin(request.NewPin);
        var recoveryKey = PinSecurityManager.GenerateRecoveryKey();
        var (recHash, recSalt) = PinSecurityManager.HashPin(recoveryKey);

        var updated = state with
        {
            PinHash = pinHash,
            PinSalt = pinSalt,
            RecoveryKeyHash = recHash,
            RecoveryKeySalt = recSalt,
            FailedPinAttempts = 0,
            LockoutUntilUtc = null
        };

        await _worker.WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
        _worker.WriteLog("Parent PIN configured or changed successfully.");

        return new IpcResponse(true, "Parent PIN configured successfully. Please store your recovery key safely.", GeneratedRecoveryKey: recoveryKey);
    }

    private async Task<IpcResponse> HandleRecoverPinAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(state.RecoveryKeyHash) || string.IsNullOrEmpty(state.RecoveryKeySalt))
        {
            return new IpcResponse(false, "No recovery key is configured on this system.");
        }

        if (string.IsNullOrWhiteSpace(request.RecoveryKey) || !PinSecurityManager.VerifyPin(request.RecoveryKey.Trim(), state.RecoveryKeyHash, state.RecoveryKeySalt))
        {
            return new IpcResponse(false, "Invalid recovery key.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPin) || request.NewPin.Length < 4)
        {
            return new IpcResponse(false, "New PIN must be at least 4 digits.");
        }

        var (pinHash, pinSalt) = PinSecurityManager.HashPin(request.NewPin);
        var newRecoveryKey = PinSecurityManager.GenerateRecoveryKey();
        var (recHash, recSalt) = PinSecurityManager.HashPin(newRecoveryKey);

        var updated = state with
        {
            PinHash = pinHash,
            PinSalt = pinSalt,
            RecoveryKeyHash = recHash,
            RecoveryKeySalt = recSalt,
            FailedPinAttempts = 0,
            LockoutUntilUtc = null
        };

        await _worker.WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
        _worker.WriteLog("PIN recovered successfully using recovery key.");

        return new IpcResponse(true, "PIN has been reset. Save your new recovery key.", GeneratedRecoveryKey: newRecoveryKey);
    }

    private async Task<IpcResponse> HandleUpdateProviderAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.IsActiveAt(now))
        {
            return new IpcResponse(false, "Cannot change provider while a timed lock is active.");
        }

        if (state.IsPinConfigured)
        {
            if (string.IsNullOrWhiteSpace(request.Pin) || !PinSecurityManager.VerifyPin(request.Pin, state.PinHash!, state.PinSalt!))
            {
                return new IpcResponse(false, "Valid parent PIN is required to change DNS provider.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ProviderName) || request.ProviderServers is null || request.ProviderServers.Count == 0)
        {
            return new IpcResponse(false, "Invalid provider configuration.");
        }

        try
        {
            var provider = DnsProvider.Create(request.ProviderName, request.ProviderServers);
            var updated = state with
            {
                ProviderName = provider.Name,
                ProviderServers = provider.Servers.Select(s => s.ToString()).ToArray()
            };

            await _worker.WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
            _worker.WriteLog($"DNS Provider updated to '{provider.Name}'.");
            return new IpcResponse(true, $"Provider updated to '{provider.Name}'.");
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, $"Provider validation failed: {ex.Message}");
        }
    }

    private async Task<IpcResponse> HandleActivateAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.IsActiveAt(now))
        {
            return new IpcResponse(false, "A timed lock is already active.");
        }

        if (state.IsPinConfigured)
        {
            if (string.IsNullOrWhiteSpace(request.Pin) || !PinSecurityManager.VerifyPin(request.Pin, state.PinHash!, state.PinSalt!))
            {
                return new IpcResponse(false, "Incorrect parent PIN.");
            }
        }

        TimeSpan duration;
        if (request.DurationDays.HasValue && request.DurationDays.Value > 0)
        {
            duration = TimeSpan.FromDays(request.DurationDays.Value);
        }
        else if (request.DurationMinutes.HasValue && request.DurationMinutes.Value > 0)
        {
            duration = TimeSpan.FromMinutes(request.DurationMinutes.Value);
        }
        else if (request.CustomExpiresUtc.HasValue && request.CustomExpiresUtc.Value > now)
        {
            duration = request.CustomExpiresUtc.Value - now;
        }
        else
        {
            return new IpcResponse(false, "Invalid lock duration specified.");
        }

        var expiryAction = request.ExpiryAction ?? ExpiryAction.KeepFilteredDns;
        var provider = DnsProvider.Create(state.ProviderName, state.ProviderServers);

        var adapters = await _dnsController.GetActivePhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);
        if (adapters.Count == 0)
        {
            return new IpcResponse(false, "No active physical network adapters (Ethernet or Wi-Fi) were found.");
        }

        var snapshots = new List<AdapterDnsSnapshot>();
        foreach (var adapter in adapters)
        {
            var snapshot = await _dnsController.CaptureSnapshotAsync(adapter.InterfaceIndex, adapter.Name, cancellationToken).ConfigureAwait(false);
            snapshots.Add(snapshot);
        }

        var newState = ProtectionStateFactory.Activate(
            now,
            now.Add(duration),
            expiryAction,
            provider,
            snapshots,
            state.PinHash,
            state.PinSalt,
            state.RecoveryKeyHash,
            state.RecoveryKeySalt);

        await _worker.WriteStateAsync(newState, cancellationToken).ConfigureAwait(false);

        // Apply DNS
        foreach (var adapter in adapters)
        {
            await _dnsController.ApplyAsync(adapter.InterfaceIndex, provider, cancellationToken).ConfigureAwait(false);
        }

        // Apply Firewall rules & Browser Policies
        await _firewallController.ApplyProtectionAsync(provider.Servers, cancellationToken).ConfigureAwait(false);
        _browserController.ApplyBrowserPolicies();

        _worker.WriteLog($"Timed lock activated for {duration} on {adapters.Count} adapter(s). Expires at {newState.ExpiresUtc:O}.");
        return new IpcResponse(true, "Protection lock activated successfully.", Status: BuildStatusResponse(newState, now).Status);
    }

    private async Task<IpcResponse> HandleEarlyUnlockAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!state.IsActiveAt(now))
        {
            return new IpcResponse(false, "No timed lock is currently active.");
        }

        if (state.IsPinConfigured)
        {
            if (string.IsNullOrWhiteSpace(request.Pin) || !PinSecurityManager.VerifyPin(request.Pin, state.PinHash!, state.PinSalt!))
            {
                return new IpcResponse(false, "Valid parent PIN is required to unlock protection early.");
            }
        }

        // Restore DNS if configured
        if (state.ExpiryAction == ExpiryAction.RestorePreviousDns)
        {
            await _dnsController.RestoreAsync(state.PreviousDns, cancellationToken).ConfigureAwait(false);
        }

        // Remove firewall and browser policies
        await _firewallController.RemoveProtectionAsync(cancellationToken).ConfigureAwait(false);
        _browserController.RemoveBrowserPolicies();

        var unlockedState = state with
        {
            Status = ProtectionStatus.Inactive,
            StartedUtc = null,
            ExpiresUtc = null,
            ExpiryAction = null,
            PreviousDns = [],
            FailedPinAttempts = 0,
            LockoutUntilUtc = null
        };

        await _worker.WriteStateAsync(unlockedState, cancellationToken).ConfigureAwait(false);
        _worker.WriteLog("Lock released early by parent authorization.");

        return new IpcResponse(true, "Protection lock released early.", Status: BuildStatusResponse(unlockedState, now).Status);
    }

    private async Task<IpcResponse> HandleGetDiagnosticsAsync(ProtectionState state, CancellationToken cancellationToken)
    {
        var adapters = await _dnsController.GetActivePhysicalAdaptersAsync(cancellationToken).ConfigureAwait(false);
        var adapterDiagList = new List<AdapterDiagnostic>();
        var provider = DnsProvider.Create(state.ProviderName, state.ProviderServers);
        var expectedIps = provider.Servers.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in adapters)
        {
            var currentDns = await _dnsController.ReadAsync(adapter.InterfaceIndex, cancellationToken).ConfigureAwait(false);
            var dnsSet = currentDns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var matches = expectedIps.All(ip => dnsSet.Contains(ip));
            var hasV4 = currentDns.Any(ip => IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            var hasV6 = currentDns.Any(ip => IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);

            adapterDiagList.Add(new AdapterDiagnostic(
                adapter.InterfaceIndex,
                adapter.Name,
                "Up",
                currentDns,
                hasV4,
                hasV6,
                matches));
        }

        var fwActive = await _firewallController.IsActiveAsync(cancellationToken).ConfigureAwait(false);
        var fwRules = await _firewallController.GetActiveRulesAsync(cancellationToken).ConfigureAwait(false);
        var browserActive = _browserController.ArePoliciesApplied();
        var browserPolicies = _browserController.GetPolicyStatus();
        var logTail = _worker.GetLogTail(25);

        var diag = new DiagnosticsDto(
            adapterDiagList,
            fwActive,
            fwRules,
            browserActive,
            browserPolicies,
            logTail);

        return new IpcResponse(true, "Diagnostics gathered.", Diagnostics: diag);
    }

    private async Task<IpcResponse> HandleAuthorizeUninstallAsync(ProtectionState state, IpcRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.IsPinConfigured)
        {
            if (string.IsNullOrWhiteSpace(request.Pin) || !PinSecurityManager.VerifyPin(request.Pin, state.PinHash!, state.PinSalt!))
            {
                return new IpcResponse(false, "Parent PIN is required to authorize uninstallation.");
            }
        }

        var authUntil = now.AddMinutes(10);
        var updated = state with { UninstallAuthorizedUntilUtc = authUntil };
        await _worker.WriteStateAsync(updated, cancellationToken).ConfigureAwait(false);
        _worker.WriteLog("Uninstallation authorized for 10 minutes.");

        return new IpcResponse(true, "Uninstallation authorized for the next 10 minutes.");
    }
}
