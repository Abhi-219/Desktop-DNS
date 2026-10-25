[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramFiles\FamilyDnsLock"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "   Family DNS Lock - Installer & Service Setup " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

# Check for Administrator elevation
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This installer requires an elevated Windows Administrator PowerShell prompt."
    exit 1
}

$repoRoot = $PSScriptRoot
$sourceApp = "$repoRoot\src\FamilyDnsLock.App\bin\Release\net10.0-windows"
$sourceService = "$repoRoot\src\FamilyDnsLock.Service\bin\Release\net10.0-windows"

# Fallback to Debug if Release not built yet
if (-not (Test-Path "$sourceService\FamilyDnsLock.Service.exe")) {
    $sourceApp = "$repoRoot\src\FamilyDnsLock.App\bin\Debug\net10.0-windows"
    $sourceService = "$repoRoot\src\FamilyDnsLock.Service\bin\Debug\net10.0-windows"
}

if (-not (Test-Path "$sourceService\FamilyDnsLock.Service.exe")) {
    Write-Host "Building solution first..." -ForegroundColor Yellow
    & "$repoRoot\build.ps1"
}

Write-Host "`n[1/5] Stopping existing service if running..." -ForegroundColor Yellow
$service = Get-Service -Name "FamilyDnsLock" -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -eq 'Running') {
        Stop-Service -Name "FamilyDnsLock" -Force
        Start-Sleep -Seconds 2
    }
}

Write-Host "[2/5] Installing application files to $InstallPath..." -ForegroundColor Yellow
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}

Copy-Item -Path "$sourceService\*" -Destination $InstallPath -Recurse -Force
Copy-Item -Path "$sourceApp\*" -Destination $InstallPath -Recurse -Force

Write-Host "[3/5] Securing persistent configuration directory..." -ForegroundColor Yellow
$dataDir = "$env:ProgramData\FamilyDnsLock"
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

# Grant Full Control only to SYSTEM and Administrators, removing standard user inheritance
& icacls.exe $dataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" /T /C | Out-Null

Write-Host "[4/5] Registering Windows background service..." -ForegroundColor Yellow
$serviceExe = "$InstallPath\FamilyDnsLock.Service.exe"
if (-not $service) {
    & sc.exe create "FamilyDnsLock" binPath= "`"$serviceExe`"" start= auto DisplayName= "Family DNS Lock Enforcement Service" | Out-Null
} else {
    & sc.exe config "FamilyDnsLock" binPath= "`"$serviceExe`"" start= auto DisplayName= "Family DNS Lock Enforcement Service" | Out-Null
}

# Configure failure actions: restart service after 5s, 10s, 20s
& sc.exe failure "FamilyDnsLock" reset= 86400 actions= restart/5000/restart/10000/restart/20000 | Out-Null
& sc.exe description "FamilyDnsLock" "Monitors and continuously enforces family-safe DNS filtering across active network adapters." | Out-Null

Write-Host "Starting Windows service..." -ForegroundColor Yellow
Start-Service -Name "FamilyDnsLock"
Start-Sleep -Seconds 2

Write-Host "[5/5] Creating Desktop and Start Menu shortcuts..." -ForegroundColor Yellow
$appExe = "$InstallPath\FamilyDnsLock.App.exe"
$wsh = New-Object -ComObject WScript.Shell

# Desktop shortcut
$desktopShortcut = $wsh.CreateShortcut("$env:PUBLIC\Desktop\Family DNS Lock.lnk")
$desktopShortcut.TargetPath = $appExe
$desktopShortcut.Description = "Family DNS Lock Desktop Interface"
$desktopShortcut.Save()

# Start Menu shortcut
$startMenuDir = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Family DNS Lock"
if (-not (Test-Path $startMenuDir)) {
    New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
}
$startMenuShortcut = $wsh.CreateShortcut("$startMenuDir\Family DNS Lock.lnk")
$startMenuShortcut.TargetPath = $appExe
$startMenuShortcut.Description = "Family DNS Lock Desktop Interface"
$startMenuShortcut.Save()

Write-Host "`n================================================" -ForegroundColor Cyan
Write-Host " Installation completed successfully!" -ForegroundColor Green
Write-Host " Service Name: FamilyDnsLock (Running, Automatic)" -ForegroundColor White
Write-Host " UI Executable: $appExe" -ForegroundColor White
Write-Host "================================================" -ForegroundColor Cyan
