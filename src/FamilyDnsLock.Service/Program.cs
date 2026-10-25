using System.IO.Pipes;
using System.Text.Json;
using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal static class Program
{
    private const string ServiceName = "FamilyDnsLock";
    private const string PipeName = "FamilyDnsLockPipe";

    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args[0].Equals("--console", StringComparison.OrdinalIgnoreCase))
            {
                var worker = new EnforcementWorker();
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    cancellation.Cancel();
                };
                Console.WriteLine("Family DNS Lock running in interactive console mode. Press Ctrl+C to stop.");
                worker.RunAsync(cancellation.Token).GetAwaiter().GetResult();
                return;
            }

            if (args[0].Equals("--status", StringComparison.OrdinalIgnoreCase))
            {
                QueryStatusAsync().GetAwaiter().GetResult();
                return;
            }

            if (args[0].Equals("--authorize-uninstall", StringComparison.OrdinalIgnoreCase))
            {
                var pin = args.Length > 1 ? args[1] : null;
                AuthorizeUninstallAsync(pin).GetAwaiter().GetResult();
                return;
            }

            if (args[0].Equals("--activate-days", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("--activate-minutes", StringComparison.OrdinalIgnoreCase))
            {
                var activateMinutes = args[0].Equals("--activate-minutes", StringComparison.OrdinalIgnoreCase);
                var maximumDuration = activateMinutes ? 1_440 : 90;
                if (args.Length < 3 || !int.TryParse(args[1], out var duration) || duration is < 1 || duration > maximumDuration ||
                    !Enum.TryParse<ExpiryAction>(args[2], ignoreCase: true, out var expiryAction) ||
                    !Enum.IsDefined(expiryAction))
                {
                    Console.Error.WriteLine("Usage: FamilyDnsLock.Service.exe --activate-days <1-90> <KeepFilteredDns|RestorePreviousDns> [PIN]");
                    Console.Error.WriteLine("   or: FamilyDnsLock.Service.exe --activate-minutes <1-1440> <KeepFilteredDns|RestorePreviousDns> [PIN]");
                    Environment.ExitCode = 2;
                    return;
                }

                var pin = args.Length > 3 ? args[3] : null;
                ActivateViaIpcAsync(activateMinutes ? null : duration, activateMinutes ? duration : null, expiryAction, pin).GetAwaiter().GetResult();
                return;
            }
        }

        var serviceWorker = new EnforcementWorker();
        WindowsServiceDispatcher.Run(ServiceName, serviceWorker.RunAsync);
    }

    private static async Task QueryStatusAsync()
    {
        try
        {
            var request = new IpcRequest(IpcCommandType.GetStatus);
            var response = await SendIpcRequestAsync(request).ConfigureAwait(false);
            if (!response.Success || response.Status is null)
            {
                Console.WriteLine($"Service responded: {response.Message}");
                return;
            }

            var s = response.Status;
            Console.WriteLine($"Status: {s.Status}");
            Console.WriteLine($"Active: {s.IsActive}");
            Console.WriteLine($"Provider: {s.ProviderName} ({string.Join(", ", s.ProviderServers)})");
            if (s.StartedUtc.HasValue) Console.WriteLine($"Started UTC: {s.StartedUtc.Value:O}");
            if (s.ExpiresUtc.HasValue) Console.WriteLine($"Expires UTC: {s.ExpiresUtc.Value:O}");
            if (s.Remaining.HasValue) Console.WriteLine($"Remaining: {s.Remaining.Value}");
            Console.WriteLine($"PIN configured: {s.IsPinConfigured}");
            Console.WriteLine($"Firewall enforced: {s.FirewallEnforced}");
            Console.WriteLine($"Browser DoH blocked: {s.BrowserPolicyEnforced}");
            Console.WriteLine($"Uninstall authorized: {s.UninstallAuthorized}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not connect to Family DNS Lock service: {ex.Message}");
            Console.Error.WriteLine("Ensure the Windows service is running.");
            Environment.ExitCode = 1;
        }
    }

    private static async Task ActivateViaIpcAsync(int? days, int? minutes, ExpiryAction expiryAction, string? pin)
    {
        try
        {
            var request = new IpcRequest(
                IpcCommandType.Activate,
                Pin: pin,
                DurationDays: days,
                DurationMinutes: minutes,
                ExpiryAction: expiryAction);

            var response = await SendIpcRequestAsync(request).ConfigureAwait(false);
            if (response.Success)
            {
                Console.WriteLine($"Protection lock activated successfully. Expires: {response.Status?.ExpiresUtc:O}");
            }
            else
            {
                Console.Error.WriteLine($"Activation failed: {response.Message}");
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not communicate with background service: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task AuthorizeUninstallAsync(string? pin)
    {
        try
        {
            var request = new IpcRequest(IpcCommandType.AuthorizeUninstall, Pin: pin);
            var response = await SendIpcRequestAsync(request).ConfigureAwait(false);
            if (response.Success)
            {
                Console.WriteLine(response.Message);
            }
            else
            {
                Console.Error.WriteLine($"Uninstall authorization failed: {response.Message}");
                Environment.ExitCode = 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Service error: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    public static async Task<IpcResponse> SendIpcRequestAsync(IpcRequest request)
    {
        using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(client);
        using var writer = new StreamWriter(client) { AutoFlush = true };

        var requestJson = JsonSerializer.Serialize(request);
        await writer.WriteLineAsync(requestJson).ConfigureAwait(false);

        var responseJson = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return new IpcResponse(false, "Empty response from service.");
        }

        return JsonSerializer.Deserialize<IpcResponse>(responseJson)
            ?? new IpcResponse(false, "Failed to parse service response.");
    }
}
