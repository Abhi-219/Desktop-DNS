# Family DNS Lock: Troubleshooting & Comprehensive Testing Guide

This document provides a troubleshooting guide for administrators and parents, followed by explicit test procedures covering all 17 requirements defined in Section 11.

---

## Part 1: Operational Troubleshooting

### 1. UI Shows "Background Service Disconnected / OFFLINE"
- **Cause**: The `FamilyDnsLock` Windows background service is stopped or failed to start.
- **Resolution**:
  Open an elevated PowerShell prompt and check service status:
  ```powershell
  Get-Service -Name "FamilyDnsLock"
  ```
  If stopped:
  ```powershell
  Start-Service -Name "FamilyDnsLock"
  ```
  Check the service error log at `C:\ProgramData\FamilyDnsLock\service.log`.

### 2. DNS Verification Mismatch on Adapter
- **Cause**: A third-party VPN client, security suite, or manual network change altered the adapter's DNS settings.
- **Resolution**:
  The background service immediately re-applies the family DNS and checks every second for 45 minutes after DNS, firewall, or browser-policy drift is detected. It then returns to its normal 30-second check interval. Network address changes also trigger an immediate check.

### 3. Parent Forgot the PIN
- **Cause**: Forgotten PIN prevents early unlock or settings modification.
- **Resolution**:
  1. Open the application and go to the **Parent Settings** tab.
  2. In the **Forgot PIN? Recover with Recovery Key** section:
     - Enter the 24-character recovery key generated during setup (e.g., `FDNS-XXXX-XXXX-XXXX`).
     - Enter a new PIN.
     - Click **Reset PIN via Recovery Key**.

### 4. Incorrect PIN Lockout Delay
- **Cause**: Rate limiting triggered by 3 or more consecutive incorrect PIN attempts.
- **Resolution**:
  - 3 incorrect attempts: 5-second delay.
  - 4 incorrect attempts: 30-second delay.
  - 5 incorrect attempts: 2-minute lockout.
  - 6+ incorrect attempts: 10-minute lockout.
  Wait for the lockout countdown timer on the screen to expire, then enter the correct PIN.

### 5. Developer VPN or Docker Conflicts
- **Cause**: Some corporate VPNs create virtual TAP/TUN adapters.
- **Resolution**:
  Family DNS Lock explicitly targets active *physical* network adapters (`Get-NetAdapter -Physical`). Virtual adapters created by Docker, WSL2 (`vEthernet`), and Hyper-V are unaffected.

---

## Part 2: Comprehensive Manual & Automated Test Procedures (17 Test Cases)

| # | Test Scenario | Verification Procedure | Expected Outcome |
|---|---|---|---|
| **1** | **Clean installation and initial setup** | Run `.\Install.ps1` from elevated PowerShell. Check service status via `Get-Service FamilyDnsLock`. Launch WPF App. | Service starts automatically in `Running` state. Desktop App connects to IPC and displays `Inactive`. |
| **2** | **Activation of a 7-day lock** | In the UI, select `7 Days`, choose `Keep Filtered DNS`, enter PIN (if set), and click Activate. | Status changes to `PROTECTION ACTIVE`. Countdown shows `6d 23h ...`. DNS addresses on physical adapters reflect `185.228.168.168`. |
| **3** | **DNS enforcement after a Windows reboot** | Activate a lock. Reboot Windows. Before opening the UI, run `Get-DnsClientServerAddress -InterfaceIndex <idx>` in PowerShell. | CleanBrowsing Family DNS remains applied immediately upon boot via the automatic background service. |
| **4** | **Ethernet and Wi-Fi connections** | Connect via Ethernet, activate lock, disconnect Ethernet and enable Wi-Fi. | Service receives `NetworkAddressChanged` event and immediately applies family DNS to the Wi-Fi adapter. |
| **5** | **Switching to a phone hotspot** | Disconnect from home Wi-Fi and connect to an Android/iPhone Wi-Fi hotspot. | Service detects network change within 1.5 seconds and locks the hotspot connection to CleanBrowsing DNS. |
| **6** | **IPv4 and IPv6 DNS configuration** | Inspect adapter DNS: `Get-DnsClientServerAddress -InterfaceIndex <idx>`. | Both IPv4 (`185.228.168.168`, `185.228.169.168`) and IPv6 (`2a0d:2a00:1::`, `2a0d:2a00:2::`) are verified in the stack. |
| **7** | **Attempted DNS changes** | While locked, manually change DNS in Windows Settings or PowerShell: `Set-DnsClientServerAddress -InterfaceIndex <idx> -ServerAddresses '8.8.8.8'`. | The service detects mismatch, restores family DNS, and checks every second for 45 minutes; it then returns to 30-second checks. Network address changes also trigger an immediate check. |
| **8** | **Supported browser Secure DNS configurations** | Open Chrome (`chrome://settings/security`), Edge (`edge://settings/privacy`), or Firefox. Check Secure DNS / DoH options. | Secure DNS is disabled or enforced via Enterprise Policy (`DnsOverHttpsMode = "off"`). Browsers cannot bypass system DNS. |
| **9** | **Normal application closure** | Close the WPF desktop window (`FamilyDnsLock.App`). Run test DNS lookups. | Enforcement continues without interruption in the background Windows service. |
| **10** | **Service failure and recovery** | Open Task Manager as Administrator. Kill `FamilyDnsLock.Service.exe`. | Windows Service Control Manager triggers auto-recovery action within 5 seconds and restarts the service. |
| **11** | **Incorrect PIN attempts** | Attempt early unlock with wrong PIN 3 times, then 4 times, then 5 times. | Rate limiter imposes 5s delay on 3rd attempt, 30s on 4th attempt, and 2m on 5th attempt. Rapid brute forcing is prevented. |
| **12** | **Parent-authorized early unlock** | Click `Unlock Early`, enter the correct PIN, and confirm. | Protection status reverts to `Inactive`. Firewall rules and browser policies are removed immediately. |
| **13** | **Automatic expiry** | Activate a 1-minute test lock (`--activate-minutes 1 KeepFilteredDns`). Wait 65 seconds. | Service automatically transitions state to `ExpiredFilteredDnsRetained` and releases the application lock. |
| **14** | **Standard uninstallation during active lock** | While lock is active, execute `.\Uninstall.ps1` without providing a PIN. | Uninstaller refuses to uninstall, prompts for parent PIN, and terminates with exit code 1 if incorrect. |
| **15** | **Attempts to stop service as local administrator** | Run `sc.exe stop FamilyDnsLock` as admin. | Documented administrative behavior: Service stops, but Windows recovery policy triggers auto-restart. Full admin limits documented in architecture guide. |
| **16** | **Compatibility with development tools** | Test `git fetch`, `npm install`, `docker run hello-world`, and `curl http://localhost:3000`. | All developer workflows, local servers, WSL, and package managers function cleanly without port or proxy interference. |
| **17** | **Windows updates and network adapter changes** | Disable and re-enable the Wi-Fi adapter via Device Manager / Network Connections. | Service receives interface status notification and re-applies CleanBrowsing DNS as soon as the adapter returns to `Up`. |

---

## Part 3: Running Automated Tests

Run the test suite directly from PowerShell:
```powershell
Set-Location "C:\Users\A\Documents\Desktop-DNS"
& dotnet run --project .\tests\FamilyDnsLock.Tests\FamilyDnsLock.Tests.csproj
```
All 15 comprehensive unit & integration checks will execute and report `[PASS]` results.
