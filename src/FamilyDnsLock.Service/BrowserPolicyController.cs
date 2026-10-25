using Microsoft.Win32;

namespace FamilyDnsLock.Service;

internal sealed class BrowserPolicyController
{
    private const string EdgePolicyPath = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ChromePolicyPath = @"SOFTWARE\Policies\Google\Chrome";
    private const string BravePolicyPath = @"SOFTWARE\Policies\BraveSoftware\Brave";
    private const string FirefoxPolicyPath = @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS";

    private const string ValueNameDnsOverHttpsMode = "DnsOverHttpsMode";

    public void ApplyBrowserPolicies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Edge
            using (var key = Registry.LocalMachine.CreateSubKey(EdgePolicyPath, writable: true))
            {
                key?.SetValue(ValueNameDnsOverHttpsMode, "off", RegistryValueKind.String);
            }

            // Chrome
            using (var key = Registry.LocalMachine.CreateSubKey(ChromePolicyPath, writable: true))
            {
                key?.SetValue(ValueNameDnsOverHttpsMode, "off", RegistryValueKind.String);
            }

            // Brave
            using (var key = Registry.LocalMachine.CreateSubKey(BravePolicyPath, writable: true))
            {
                key?.SetValue(ValueNameDnsOverHttpsMode, "off", RegistryValueKind.String);
            }

            // Firefox
            using (var key = Registry.LocalMachine.CreateSubKey(FirefoxPolicyPath, writable: true))
            {
                key?.SetValue("Enabled", 0, RegistryValueKind.DWord);
                key?.SetValue("Locked", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to apply browser policies: {ex.Message}", ex);
        }
    }

    public void RemoveBrowserPolicies()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Remove values we set
            using (var key = Registry.LocalMachine.OpenSubKey(EdgePolicyPath, writable: true))
            {
                key?.DeleteValue(ValueNameDnsOverHttpsMode, throwOnMissingValue: false);
            }

            using (var key = Registry.LocalMachine.OpenSubKey(ChromePolicyPath, writable: true))
            {
                key?.DeleteValue(ValueNameDnsOverHttpsMode, throwOnMissingValue: false);
            }

            using (var key = Registry.LocalMachine.OpenSubKey(BravePolicyPath, writable: true))
            {
                key?.DeleteValue(ValueNameDnsOverHttpsMode, throwOnMissingValue: false);
            }

            using (var key = Registry.LocalMachine.OpenSubKey(FirefoxPolicyPath, writable: true))
            {
                key?.DeleteValue("Enabled", throwOnMissingValue: false);
                key?.DeleteValue("Locked", throwOnMissingValue: false);
            }
        }
        catch
        {
            // Best effort removal
        }
    }

    public bool ArePoliciesApplied()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var edge = Registry.LocalMachine.OpenSubKey(EdgePolicyPath);
            var edgeVal = edge?.GetValue(ValueNameDnsOverHttpsMode)?.ToString();
            return string.Equals(edgeVal, "off", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyDictionary<string, string> GetPolicyStatus()
    {
        var dict = new Dictionary<string, string>();
        if (!OperatingSystem.IsWindows())
        {
            return dict;
        }

        try
        {
            using var edge = Registry.LocalMachine.OpenSubKey(EdgePolicyPath);
            dict["Edge DnsOverHttpsMode"] = edge?.GetValue(ValueNameDnsOverHttpsMode)?.ToString() ?? "Not configured";

            using var chrome = Registry.LocalMachine.OpenSubKey(ChromePolicyPath);
            dict["Chrome DnsOverHttpsMode"] = chrome?.GetValue(ValueNameDnsOverHttpsMode)?.ToString() ?? "Not configured";

            using var brave = Registry.LocalMachine.OpenSubKey(BravePolicyPath);
            dict["Brave DnsOverHttpsMode"] = brave?.GetValue(ValueNameDnsOverHttpsMode)?.ToString() ?? "Not configured";

            using var firefox = Registry.LocalMachine.OpenSubKey(FirefoxPolicyPath);
            dict["Firefox DNSOverHTTPS Enabled"] = firefox?.GetValue("Enabled")?.ToString() ?? "Not configured";
        }
        catch (Exception ex)
        {
            dict["Error"] = ex.Message;
        }

        return dict;
    }
}
