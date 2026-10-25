# Family DNS Lock: Architecture & Security Limitations

## 1. System Architecture

Family DNS Lock is built with a separated, multi-tiered architecture designed specifically for Windows 10 and Windows 11 (64-bit):

```
+-----------------------------------------------------------------------+
|                         Desktop User Interface                        |
|                  (WPF .NET 10 Desktop Application)                    |
| - Protection status, countdown timer, activation wizard               |
| - Parent PIN modal, settings, early-unlock, diagnostics UI            |
+-----------------------------------------------------------------------+
                                   |
         Named Pipe IPC (\\.\pipe\FamilyDnsLockPipe with Windows ACL)
                                   |
+-----------------------------------------------------------------------+
|                    Windows Background Enforcement Service             |
|                       (LocalSystem Service Host)                      |
| - Continuous enforcement loop (30s heartbeat + network change hooks)  |
| - DNS stack management (PowerShell / WMI / NetAdapter)                |
| - Windows Firewall rule enforcement (Port 853 DoT & Port 53 bypass)   |
| - Browser Group Policy enforcement (Chrome, Edge, Brave, Firefox DoH) |
| - State persistence & atomic file writes with strict ACLs             |
+-----------------------------------------------------------------------+
        |                                                 |
+-------------------------------+       +-------------------------------+
|     Network & OS Stack        |       |    Protected Storage          |
| - Physical Ethernet / Wi-Fi   |       | C:\ProgramData\FamilyDnsLock\ |
| - Windows Filtering Platform  |       | - state.json (ACL: SYSTEM+Adm)|
| - Registry Policies (HKLM)    |       | - service.log (diagnostics)   |
+-------------------------------+       +-------------------------------+
```

### Component Roles

1. **FamilyDnsLock.Core**:
   - Contains pure domain logic: DNS provider validation (requiring at least one IPv4 and one IPv6 address), lock state management, UTC time normalization, clock rollback detection, PBKDF2 HMAC-SHA256 PIN hashing with unique 32-byte salts, 24-character Base32 recovery keys, progressive rate limiting, and IPC message schemas.
   - Zero commercial or unnecessary third-party dependencies.

2. **FamilyDnsLock.Service**:
   - Runs as a Windows Service under the `NT AUTHORITY\SYSTEM` security context.
   - Self-healing enforcement engine: checks adapter DNS every 30 seconds, immediately reacts to `NetworkChange.NetworkAddressChanged` events when switching between Wi-Fi and phone hotspots, and re-applies family DNS if altered.
   - Hosts the asynchronous Named Pipe IPC server, enforcing pipe security allowing only Administrators, SYSTEM, and authorized processes.
   - Manages Windows Advanced Firewall rules and Browser Enterprise Policies.

3. **FamilyDnsLock.App**:
   - Modern WPF desktop client providing a dashboard, live countdown, PIN management, diagnostics view, and early-unlock flows.
   - Completely decoupled from the service: closing or killing the UI process does **not** terminate or disrupt the background Windows service enforcement.

---

## 2. Security Boundaries & Administrative Limitations

### The Local Administrator Dilemma

In the Windows NT security architecture, a user with **Local Administrator** privileges (`BUILTIN\Administrators`) possesses full rights over the local operating system, including:
- `SeDebugPrivilege` (can attach to and terminate any non-protected process).
- `SeTakeOwnershipPrivilege` (can take ownership of any file, directory, or registry key, regardless of ACLs).
- Access to the Windows Service Control Manager (`sc.exe stop`, `sc.exe delete`).
- Ability to boot Windows into Safe Mode or Windows Recovery Environment (WinRE) where third-party services and firewall drivers do not load.

### Enforcement Classification

To be transparent, reliable, and compliant with documented Windows security principles, protections are categorized into three levels:

| Mechanism | Enforceable Against Standard User? | Best-Effort Protection Against Local Admin? | Requires External / Enterprise MDM (Intune)? |
|---|---|---|---|
| **Continuous DNS Restoration** | Yes (100% enforced) | Yes (Restores within 30s or on network event if modified) | Yes (if admin disables service) |
| **Protected Storage ACLs** | Yes (Standard user cannot read/write `C:\ProgramData\FamilyDnsLock`) | Yes (Admin must take explicit ownership to modify) | Yes |
| **Parent PIN Protection** | Yes (PBKDF2 SHA-256 with rate limiting prevents brute force) | Yes (Prevents UI settings disablement and unauthorized uninstall) | Yes |
| **Port 853 (DoT) & Port 53 Block** | Yes (Managed via Windows Firewall) | Yes (Re-applied automatically if removed) | Yes |
| **Browser DoH Policy** | Yes (Locks browser Secure DNS in HKLM) | Yes (Re-applied automatically if deleted) | Yes |
| **Uninstall Restriction** | Yes | Yes (Script and service require parent PIN verification) | Yes (Preventing safe-mode removal) |

### Recommended Family Configuration
For robust protection when the family member is a software engineer:
1. **Account Separation**: Configure the software engineer's daily account as a **Standard User**. Create a separate `FamilyAdmin` local account with a strong parent password. The engineer can still install dev tools and run admin tasks using standard Windows UAC ("Run as administrator" / credentials prompt) without having unrestricted continuous root access.
2. **BitLocker Drive Encryption**: Enable BitLocker and ensure only the parent retains the BitLocker Recovery Key to prevent offline disk access via Linux USB or Windows PE.
3. **UEFI / BIOS Password & Secure Boot**: Password-protect UEFI settings and enable Secure Boot to prevent booting from external USB recovery drives.

---

## 3. Clock Tampering & Expiry Limitations

- Family DNS Lock stores all timestamps (`StartedUtc`, `ExpiresUtc`) in normalized UTC.
- When evaluating lock status, the service compares current system UTC against `StartedUtc`. If the clock has shifted backwards (`currentUtc < startedUtc`), it flags clock rollback, logs the incident, and continues active enforcement.
- **Limitation**: If a local administrator advances the system clock into the future past `ExpiresUtc`, the local clock indicates that the lock duration has expired. In standalone offline systems without domain/Active Directory time synchronization, the local RTC is controllable by an administrator. For long-term protection, pairing with home router DNS or enterprise policy is recommended.

---

## 4. Developer Workflow Compatibility

The application is specifically engineered not to disrupt developer environments:
- **Localhost Development**: Loopback addresses (`127.0.0.1`, `::1`) are preserved. Local development servers, Vite, Webpack, Node.js, and local proxies operate normally.
- **WSL 2 & Hyper-V**: WSL internal virtual switches and Hyper-V NAT adapters are distinct from physical Ethernet and Wi-Fi adapters (`Get-NetAdapter -Physical`). The enforcement engine explicitly targets physical adapters only, ensuring internal VM networking is untouched.
- **Docker Desktop**: Docker DNS routing inside container networks remains functional.
- **Git & Package Managers**: Git, NuGet, npm, pip, and Cargo resolve domains through system DNS over port 443/80, which resolves cleanly through CleanBrowsing family servers.
