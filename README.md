# Family DNS Lock

Family DNS Lock is an in-progress Windows application intended to apply a family-safe DNS provider for a chosen period. This repository currently contains the domain logic, a Windows service host, a command-line activation path, and a small dependency-free test runner. It is not yet a complete parental-control product.

## Current Status

Implemented source:

- CleanBrowsing Family Filter IPv4 and IPv6 addresses.
- Validation requiring at least one IPv4 and one IPv6 address for a custom provider.
- Timed lock-state model and explicit expiry choices: retain filtered DNS or restore saved DNS addresses.
- Windows PowerShell DNS operations for active physical network adapters, with DNS readback verification.
- A service worker that checks for active lock state every 30 seconds and reapplies DNS.
- A command-line activation mode that requires an elevated Windows administrator session, snapshots the current DNS server addresses, applies the filter, verifies the settings, and records the lock state.
- A no-package console test project for the core state and provider rules.

Not implemented yet:

- WPF interface, parent PIN and recovery flow, secure IPC, installer, service installation/update/uninstallation workflow, notifications, clock-change policy, browser policies, firewall rules, and complete diagnostics.
- Automatic preservation of whether each original DNS configuration was DHCP-provided or static. The current snapshot records observed addresses only. Restoring those addresses may make them static rather than restoring DHCP behavior. **Do not use `RestorePreviousDns` on a machine where exact restoration is required.**
- Integration and manual results on real Windows network adapters. The core test source exists, but its tests have not been run in this environment.

The project targets .NET 10. The current workspace machine has a .NET 8 runtime but no .NET SDK, so these commands require a .NET 10 SDK already installed. No SDK or packages are downloaded by this project.

## Build And Test

From the repository root in PowerShell, with a .NET 10 SDK installed:

```powershell
dotnet build .\FamilyDnsLock.sln
dotnet run --project .\tests\FamilyDnsLock.Tests\FamilyDnsLock.Tests.csproj
```

The test runner prints a `PASS` or `FAIL` result for each check and exits with code 1 if any check fails. It does not change Windows DNS settings.

## Development Commands

Run activation from an elevated PowerShell window under the parent-controlled administrator account. This command changes DNS on active physical adapters, so use it only on a machine where that change is intended:

```powershell
dotnet run --project .\src\FamilyDnsLock.Service\FamilyDnsLock.Service.csproj -- --activate-days 7 KeepFilteredDns
```

Supported durations are 1 through 90 whole days or 1 through 1440 minutes for short tests. The expiry argument must be `KeepFilteredDns` or `RestorePreviousDns`. For example, selecting `KeepFilteredDns` stops the timed enforcement at expiry but leaves the family DNS addresses configured. `RestorePreviousDns` requests restoration from the saved address snapshot and has the DHCP caveat above.

To test a three-minute lock, open two elevated PowerShell windows. In the first, start the foreground enforcement loop:

```powershell
dotnet run --project .\src\FamilyDnsLock.Service\FamilyDnsLock.Service.csproj -- --console
```

In the second, activate the lock:

```powershell
dotnet run --project .\src\FamilyDnsLock.Service\FamilyDnsLock.Service.csproj -- --activate-minutes 3 KeepFilteredDns
```

The activation command applies and verifies DNS immediately. The foreground worker rechecks every 30 seconds, so expiry may be processed up to about 30 seconds after the requested time. `KeepFilteredDns` leaves the family DNS configured after the timed state expires. Press Ctrl+C in the first window to stop the development worker; this does not restore the previous DNS. Closing the activation window does not stop the worker.

After building, the service can be run in a foreground development mode from an elevated console:

```powershell
& .\src\FamilyDnsLock.Service\bin\Debug\net10.0-windows\FamilyDnsLock.Service.exe --console
```

This foreground mode is not a Windows service installation. Closing the console stops it. The project does not yet install or configure automatic service startup, so do not rely on it to enforce DNS after reboot.

## Runtime Data

The service stores its state and local log under:

```text
C:\ProgramData\FamilyDnsLock\state.json
C:\ProgramData\FamilyDnsLock\service.log
```

The service attempts to set the directory permissions to allow full control only to SYSTEM and the local Administrators group. This helps protect against modification from a standard account; it does not protect against a local administrator, who can change permissions, stop the service, or remove the application.

The current log records service and enforcement errors. Do not add browsing history, passwords, keystrokes, or screenshots to logging.

## Important Security And Usage Notes

- For meaningful uninstall resistance, the laptop user's daily account should be a standard account. The parent should keep a separate administrator account and securely retain its password and the BitLocker recovery key.
- A person with local administrator access can bypass or remove this application. This software does not make a lock administrator-proof, and the current milestone does not include an authorized-uninstall restriction.
- The activation command writes a lock-state file, but does not install or start the service. DNS changes may therefore remain after the activation process exits, while automatic re-enforcement will not occur unless the service is run separately.
- DNS filtering is not comprehensive web filtering. Direct DNS, encrypted DNS, VPNs, proxies, alternate networks, and direct-IP access can bypass resolver-based controls.
- Router filtering is an independent layer only on networks that use that router. A phone hotspot or other network is not covered by home-router settings.
- DNS configuration changes can affect VPNs, virtual adapters, Docker, WSL, Hyper-V, development environments, and corporate networks. The current code targets active physical adapters only, but has not been integration-tested for compatibility.
- Keep important data backed up to a separate device or service. Another partition such as D: or E: on the same physical disk is not a backup.

## Project Layout

```text
FamilyDnsLock.sln
src/
  FamilyDnsLock.Core/       Provider and timed-lock domain types
  FamilyDnsLock.Service/    Windows service host and DNS enforcement
tests/
  FamilyDnsLock.Tests/      Dependency-free core test runner
```

## Next Work

Before treating this as usable protection, the next steps are to model and restore DNS mode accurately, add automated Windows integration tests, implement parent-authorized activation and unlock flows, install the service securely with automatic startup, and build the WPF interface and installer. The browser, firewall, PIN, and recovery features need to be implemented and tested separately; they are not present yet.