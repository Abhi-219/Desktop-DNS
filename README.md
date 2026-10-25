# Family DNS Lock for Windows 10/11

*Family DNS Lock* is a complete Windows application and background enforcement service designed for parents who need to enforce family-safe DNS filtering (CleanBrowsing Family Filter) for a specified duration on a Windows 10 or Windows 11 computer, even when the computer user has Windows administrator access for software development.

---

## Key Features

1. **Dual-Stack DNS Filtering (IPv4 & IPv6)**:
   - Built-in CleanBrowsing Family Filter addresses:
     - IPv4: `185.228.168.168`, `185.228.169.168`
     - IPv6: `2a0d:2a00:1::`, `2a0d:2a00:2::`
   - Configurable custom family DNS providers requiring dual-stack (IPv4 + IPv6) validation.
   - Enforced across all active physical Ethernet and Wi-Fi network adapters.
   - Preserves original DHCP vs static settings so network roaming to other Wi-Fi networks remains seamless.

2. **Timed DNS Lock**:
   - Preset durations: 1 Day, 7 Days, 15 Days, 30 Days, 60 Days, 90 Days, or custom date/time.
   - Choice upon expiry: Keep Filtered DNS active (recommended) or restore original DNS.
   - Automatic expiry transition and UTC clock-rollback tamper detection.

3. **Parent Access Control & Security**:
   - Parent PIN protection for settings, activation, early unlock, and uninstallation.
   - Cryptographically secured with PBKDF2 (HMAC-SHA256, 100,000 iterations, 32-byte salt).
   - Progressive rate limiting and lockouts preventing brute-force attempts.
   - 24-character Base32 Emergency PIN Recovery Key.

4. **DNS & Browser Bypass Resistance**:
   - Outbound DNS over TLS (TCP Port 853) blocked via Windows Firewall.
   - Outbound direct unapproved DNS (Port 53 UDP/TCP) blocked to rogue public resolvers.
   - Browser DNS over HTTPS (DoH) disabled via Windows Group Policy / Registry for Microsoft Edge, Google Chrome, Brave, and Mozilla Firefox.
   - Developer-friendly: Loopback (`127.0.0.1`, `::1`), Docker, WSL2, Hyper-V, and local developer proxies remain fully functional.

5. **Decoupled Windows Service & WPF Desktop UI**:
   - Background Windows Service runs as `LocalSystem` with automatic startup and auto-recovery.
   - Reactive network monitoring using `NetworkAddressChanged` for instant re-enforcement when connecting to phone hotspots or toggling Wi-Fi.
   - After DNS, firewall, or browser-policy drift is detected during an active lock, checks run every second for 45 minutes, then return to the normal 30-second interval.
   - Clean, modern WPF desktop app communicating over a secured Named Pipe IPC.

6. **Uninstallation Protection**:
   - Normal uninstallation (`Uninstall.ps1`) requires parent PIN authorization during an active lock period.

---

## Project Structure

```
Desktop-DNS/
├── FamilyDnsLock.sln           # Visual Studio solution file
├── Directory.Build.props       # Global build properties
├── build.ps1                   # Automated build and test script
├── Install.ps1                 # Service installer & shortcut creator
├── Uninstall.ps1               # Authorized uninstaller
├── src/
│   ├── FamilyDnsLock.Core/     # Domain models, PIN hashing, IPC contracts, state
│   ├── FamilyDnsLock.Service/  # Windows Service host, DNS controller, Firewall, Policies, IPC server
│   └── FamilyDnsLock.App/      # Modern WPF desktop application
├── tests/
│   └── FamilyDnsLock.Tests/    # 15 automated unit & integration test suites
└── docs/
    ├── ARCHITECTURE_AND_LIMITATIONS.md    # Security boundaries, admin limits & threat model
    ├── PARENT_GUIDE_AND_ROUTER_SETUP.md   # Parent setup, PIN guide & router configuration
    └── TROUBLESHOOTING_AND_TESTING_GUIDE.md # 17 manual test cases & operational troubleshooting
```

---

## Build and Run Tests

Requirements: Windows 10/11 64-bit and .NET 10 SDK (or higher).

```powershell
Set-Location "C:\Users\A\Documents\Desktop-DNS"

# Run automated build and test script
.\build.ps1
```

Or build and test manually:

```powershell
# Build solution
dotnet build .\FamilyDnsLock.sln -m:1

# Run automated test suite
dotnet run --project .\tests\FamilyDnsLock.Tests\FamilyDnsLock.Tests.csproj
```

---

## Installation & Setup

Run from an elevated Windows PowerShell prompt (Run as Administrator):

```powershell
Set-Location "C:\Users\A\Documents\Desktop-DNS"
.\Install.ps1
```

This will:
1. Build and install binaries to `C:\Program Files\FamilyDnsLock`.
2. Secure persistent storage directory `C:\ProgramData\FamilyDnsLock` (SYSTEM & Administrators only).
3. Register and start the `FamilyDnsLock` Windows background service (set to Automatic start with auto-restart recovery).
4. Create Desktop and Start Menu shortcuts for the WPF Desktop application.

---

## Uninstallation

To uninstall the application:

```powershell
# If a lock is active, you will be prompted for your Parent PIN:
.\Uninstall.ps1

# Or provide the PIN directly:
.\Uninstall.ps1 -Pin "849204"

# To also delete persistent logs and configuration:
.\Uninstall.ps1 -Pin "849204" -PurgeData
```

---

## Documentation

For full details, refer to the documentation:
- [Architecture & Limitations](docs/ARCHITECTURE_AND_LIMITATIONS.md) - Deep dive into Windows security boundaries, threat model, and administrator realities.
- [Parent Guide & Router Setup](docs/PARENT_GUIDE_AND_ROUTER_SETUP.md) - Configuration guide and instructions for home router DNS.
- [Troubleshooting & Testing Guide](docs/TROUBLESHOOTING_AND_TESTING_GUIDE.md) - Verification of all 17 test cases and troubleshooting steps.
