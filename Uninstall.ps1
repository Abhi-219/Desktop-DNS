[CmdletBinding()]
param(
    [string]$Pin,
    [switch]$PurgeData,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "   Family DNS Lock - Authorized Uninstaller    " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

# Check for Administrator elevation
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Uninstallation requires an elevated Windows Administrator PowerShell prompt."
    exit 1
}

$stateFile = "$env:ProgramData\FamilyDnsLock\state.json"
$isLockActive = $false
$isPinSet = $false

if (Test-Path $stateFile) {
    try {
        $stateJson = Get-Content $stateFile -Raw | ConvertFrom-Json
        $nowUtc = [DateTimeOffset]::UtcNow

        if ($stateJson.PinHash -and $stateJson.PinSalt) {
            $isPinSet = $true
        }

        if ($stateJson.Status -eq 1 -or $stateJson.Status -eq "Active") {
            if ($stateJson.ExpiresUtc) {
                $expires = [DateTimeOffset]::Parse($stateJson.ExpiresUtc)
                if ($nowUtc -lt $expires) {
                    $isLockActive = $true
                }
            }
        }

        # Check if already authorized via UI/IPC
        $isAuthorized = $false
        if ($stateJson.UninstallAuthorizedUntilUtc) {
            $authUntil = [DateTimeOffset]::Parse($stateJson.UninstallAuthorizedUntilUtc)
            if ($nowUtc -lt $authUntil) {
                $isAuthorized = $true
            }
        }

        if ($isLockActive -and -not $isAuthorized) {
            Write-Host "`n[NOTICE] A Timed Protection Lock is currently ACTIVE." -ForegroundColor Yellow
            if ($isPinSet) {
                if (-not $Pin) {
                    $Pin = Read-Host "Enter Parent PIN to authorize uninstallation"
                }

                # Attempt authorization via Service or Core
                $serviceExe = "$env:ProgramFiles\FamilyDnsLock\FamilyDnsLock.Service.exe"
                if (Test-Path $serviceExe) {
                    & $serviceExe --authorize-uninstall $Pin
                    if ($LASTEXITCODE -ne 0) {
                        Write-Error "Invalid Parent PIN. Uninstallation refused while lock is active."
                        exit 1
                    }
                } else {
                    Write-Error "Protection lock is active and parent authorization failed."
                    exit 1
                }
            } else {
                Write-Host "No PIN was configured. Proceeding with uninstall." -ForegroundColor Gray
            }
        }
    } catch {
        Write-Warning "Could not read state file: $($_.Exception.Message)"
    }
}

Write-Host "`n[1/5] Stopping and removing Windows Service..." -ForegroundColor Yellow
$service = Get-Service -Name "FamilyDnsLock" -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Stop-Service -Name "FamilyDnsLock" -Force
        Start-Sleep -Seconds 2
    }
    & sc.exe delete "FamilyDnsLock" | Out-Null
    Write-Host "Service removed." -ForegroundColor Green
} else {
    Write-Host "Service was not installed." -ForegroundColor Gray
}

Write-Host "`n[2/5] Cleaning up Windows Firewall rules..." -ForegroundColor Yellow
Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "FamilyDnsLock_*" } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Write-Host "Firewall rules removed." -ForegroundColor Green

Write-Host "`n[3/5] Cleaning up Browser Secure DNS policies..." -ForegroundColor Yellow
$policyPaths = @(
    "HKLM:\SOFTWARE\Policies\Microsoft\Edge",
    "HKLM:\SOFTWARE\Policies\Google\Chrome",
    "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave"
)
foreach ($p in $policyPaths) {
    if (Test-Path $p) {
        Remove-ItemProperty -Path $p -Name "DnsOverHttpsMode" -ErrorAction SilentlyContinue
    }
}
$ffPath = "HKLM:\SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS"
if (Test-Path $ffPath) {
    Remove-ItemProperty -Path $ffPath -Name "Enabled" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $ffPath -Name "Locked" -ErrorAction SilentlyContinue
}
Write-Host "Browser policies restored." -ForegroundColor Green

Write-Host "`n[4/5] Removing application shortcuts..." -ForegroundColor Yellow
Remove-Item -Path "$env:PUBLIC\Desktop\Family DNS Lock.lnk" -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Family DNS Lock" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n[5/5] Removing installed binaries..." -ForegroundColor Yellow
$installPath = "$env:ProgramFiles\FamilyDnsLock"
if (Test-Path $installPath) {
    Remove-Item -Path $installPath -Recurse -Force -ErrorAction SilentlyContinue
}

if ($PurgeData) {
    Write-Host "Purging application state and logs ($env:ProgramData\FamilyDnsLock)..." -ForegroundColor Yellow
    Remove-Item -Path "$env:ProgramData\FamilyDnsLock" -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "Configuration data preserved in $env:ProgramData\FamilyDnsLock (use -PurgeData to delete)." -ForegroundColor Gray
}

Write-Host "`n================================================" -ForegroundColor Cyan
Write-Host " Family DNS Lock uninstalled successfully!" -ForegroundColor Green
Write-Host "================================================" -ForegroundColor Cyan
