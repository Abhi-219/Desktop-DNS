[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "================================================" -ForegroundColor Cyan
Write-Host "   Family DNS Lock - Solution Build Script     " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan

# Locate dotnet
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
        $env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
        $env:Path = "$env:DOTNET_ROOT;$env:Path"
    } else {
        throw "dotnet SDK was not found in PATH or in $env:USERPROFILE\.dotnet"
    }
}

$repoRoot = $PSScriptRoot
Set-Location $repoRoot

Write-Host "`n[1/3] Building solution (Configuration: $Configuration)..." -ForegroundColor Yellow
& dotnet build "$repoRoot\FamilyDnsLock.sln" -c $Configuration -m:1
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
Write-Host "Build succeeded." -ForegroundColor Green

if (-not $SkipTests) {
    Write-Host "`n[2/3] Running automated tests..." -ForegroundColor Yellow
    & dotnet run --project "$repoRoot\tests\FamilyDnsLock.Tests\FamilyDnsLock.Tests.csproj" -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Automated tests failed with exit code $LASTEXITCODE."
    }
    Write-Host "All tests passed." -ForegroundColor Green
} else {
    Write-Host "`n[2/3] Tests skipped by user." -ForegroundColor Gray
}

Write-Host "`n[3/3] Publishing release artifacts to $repoRoot\publish..." -ForegroundColor Yellow
$publishDir = "$repoRoot\publish"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
New-Item -ItemType Directory -Path $publishDir | Out-Null

& dotnet publish "$repoRoot\src\FamilyDnsLock.Service\FamilyDnsLock.Service.csproj" -c $Configuration -o "$publishDir\Service" --no-build
& dotnet publish "$repoRoot\src\FamilyDnsLock.App\FamilyDnsLock.App.csproj" -c $Configuration -o "$publishDir\App" --no-build

Write-Host "`n================================================" -ForegroundColor Cyan
Write-Host " Build & Publish completed successfully!" -ForegroundColor Green
Write-Host " Artifacts published to: $publishDir" -ForegroundColor White
Write-Host "================================================" -ForegroundColor Cyan
