# Parent Configuration & Router Setup Guide

This guide explains how to configure Family DNS Lock on the Windows laptop, set up the parent PIN and recovery key, and configure your home Wi-Fi router as an independent additional layer of protection.

---

## 1. Initial Setup on Windows 10 / 11

### Step 1: Install Family DNS Lock
1. Run PowerShell as Administrator.
2. Execute the installer script:
   ```powershell
   Set-Location "C:\Users\A\Documents\Desktop-DNS"
   .\Install.ps1
   ```
3. The installer copies binaries to `C:\Program Files\FamilyDnsLock`, registers the Windows background service with automatic startup and auto-recovery, and creates a desktop shortcut.

### Step 2: Open the Desktop Application & Set Parent PIN
1. Launch **Family DNS Lock** from the Desktop or Start Menu.
2. Navigate to the **Parent Settings** tab.
3. In the **Parent Access Control (PIN)** card:
   - Enter a secure 4 to 16 digit PIN (e.g. `849204`).
   - Confirm the new PIN.
   - Click **Save Parent PIN**.
4. An **Emergency PIN Recovery Key** will appear in a green box (e.g., `FDNS-A7KB-9MX2-P4TL`).
   - **Crucial**: Copy or write down this key and store it in a secure password manager or safe. If you ever forget your PIN, this recovery key is the only way to reset it without complete uninstallation.

---

## 2. Activating a Timed Protection Lock

1. In the application, click the **Activate Lock** tab.
2. **Choose Protection Duration**:
   - Select a duration preset: **1 Day**, **7 Days**, **15 Days**, **30 Days**, **60 Days**, **90 Days**, or select **Custom Expiry** to specify an exact date and time.
3. **Choose Expiry Action**:
   - **Keep Filtered DNS active (Recommended)**: When the lock duration concludes, enforcement stops and settings unlock, but the family-safe DNS servers remain configured so the computer is not abruptly exposed to unfiltered content.
   - **Restore Previous DNS settings**: Restores the network adapters to their original dynamic DHCP or static addresses upon expiry.
4. **Enter Parent PIN**:
   - Enter your parent PIN in the confirmation box.
5. Click **Confirm & Activate Protection Lock**.
6. The dashboard will immediately show:
   - Status: **PROTECTION ACTIVE** (Green badge).
   - Live countdown timer of remaining days, hours, minutes, and seconds.
   - Start and expiry timestamps.

---

## 3. Early Unlock (Parent-Authorized)

If you need to release protection before the lock expires:
1. Open **Family DNS Lock**.
2. On the **Dashboard**, click **Unlock Early (Parent PIN)**.
3. Enter your parent PIN in the authorization modal.
4. Protection will be released, firewall rules and browser policies will be restored, and network DNS will be handled according to your configured choice.

---

## 4. Configuring Your Home Wi-Fi Router (Independent Network Layer)

Configuring your home router with the same CleanBrowsing Family Filter provides network-wide protection for all devices on the home network (smartphones, tablets, smart TVs, guest devices).

### Default CleanBrowsing Family Filter Addresses
- **IPv4 Primary**: `185.228.168.168`
- **IPv4 Secondary**: `185.228.169.168`
- **IPv6 Primary**: `2a0d:2a00:1::`
- **IPv6 Secondary**: `2a0d:2a00:2::`

### Step-by-Step Router Configuration
1. Open a web browser on any device connected to your home Wi-Fi.
2. Navigate to your router's administration page (common addresses: `192.168.1.1`, `192.168.0.1`, or `10.0.0.1`).
3. Log in with your router's administrative credentials.
4. Navigate to **WAN**, **Internet**, or **DHCP Server** settings (often under *Advanced* or *Network*).
5. Locate the **DNS Server** configuration section:
   - Change from *Automatic / ISP Provided* to *Manual* or *Static*.
   - Set **Primary DNS (IPv4)** to `185.228.168.168`.
   - Set **Secondary DNS (IPv4)** to `185.228.169.168`.
6. If your router supports IPv6:
   - Locate the **IPv6 DNS** section.
   - Set **Primary IPv6 DNS** to `2a0d:2a00:1::`.
   - Set **Secondary IPv6 DNS** to `2a0d:2a00:2::`.
7. Save / Apply changes and reboot the router.

---

## 5. Important Network Isolation Warnings

> [!WARNING]
> **Router filtering alone DOES NOT protect mobile hotspots!**
> When the laptop disconnects from your home Wi-Fi and connects to a phone hotspot, coffee shop Wi-Fi, or hotel network, the home router's DNS settings no longer apply.
> 
> Family DNS Lock on the Windows laptop automatically detects when the network changes, queries the new connection, and applies CleanBrowsing Family Filter directly onto the laptop's Wi-Fi adapter.

> [!NOTE]
> **Tethered Mobile Traffic Limitations:**
> Enabling parental filtering on an Android phone's browser does **not** filter the traffic of a laptop tethered to that phone. A phone hotspot simply acts as a network bridge for tethered laptops unless the laptop itself has DNS filtering locked (which Family DNS Lock accomplishes).
