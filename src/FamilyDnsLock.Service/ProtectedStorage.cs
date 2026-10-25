using System.Diagnostics;

namespace FamilyDnsLock.Service;

internal static class ProtectedStorage
{
    public static void EnsureDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected service storage is available only on Windows.");
        }

        Directory.CreateDirectory(directoryPath);

        var icaclsPath = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = icaclsPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(directoryPath);
        startInfo.ArgumentList.Add("/inheritance:r");
        startInfo.ArgumentList.Add("/grant:r");
        startInfo.ArgumentList.Add("*S-1-5-18:(OI)(CI)F");
        startInfo.ArgumentList.Add("*S-1-5-32-544:(OI)(CI)F");
        startInfo.ArgumentList.Add("/T");
        startInfo.ArgumentList.Add("/C");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start icacls to protect application state.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        _ = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new UnauthorizedAccessException(string.IsNullOrWhiteSpace(error)
                ? $"Could not secure application storage (icacls exit code {process.ExitCode})."
                : error.Trim());
        }

    }
}