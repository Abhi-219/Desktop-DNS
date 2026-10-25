using FamilyDnsLock.Core;

namespace FamilyDnsLock.Service;

internal static class Program
{
    private const string ServiceName = "FamilyDnsLock";

    private static void Main(string[] args)
    {
        if (args.Length > 0 &&
            (args[0].Equals("--activate-days", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--activate-minutes", StringComparison.OrdinalIgnoreCase)))
        {
            var activateMinutes = args[0].Equals("--activate-minutes", StringComparison.OrdinalIgnoreCase);
            var maximumDuration = activateMinutes ? 1_440 : 90;
            if (args.Length != 3 || !int.TryParse(args[1], out var duration) || duration is < 1 || duration > maximumDuration ||
                !Enum.TryParse<ExpiryAction>(args[2], ignoreCase: true, out var expiryAction) ||
                !Enum.IsDefined(expiryAction))
            {
                Console.Error.WriteLine("Usage: FamilyDnsLock.Service.exe --activate-days <1-90> <KeepFilteredDns|RestorePreviousDns>");
                Console.Error.WriteLine("   or: FamilyDnsLock.Service.exe --activate-minutes <1-1440> <KeepFilteredDns|RestorePreviousDns>");
                Environment.ExitCode = 2;
                return;
            }

            try
            {
                var lockDuration = activateMinutes
                    ? TimeSpan.FromMinutes(duration)
                    : TimeSpan.FromDays(duration);
                new ActivationCommand().ActivateAsync(lockDuration, expiryAction, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Activation failed: {exception.Message}");
                Environment.ExitCode = 1;
            }

            return;
        }

        var worker = new EnforcementWorker();
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            worker.RunAsync(cancellation.Token).GetAwaiter().GetResult();
            return;
        }

        WindowsServiceDispatcher.Run(ServiceName, worker.RunAsync);
    }
}