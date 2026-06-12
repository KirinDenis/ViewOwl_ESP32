#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the pinned Chrome for Testing build required by ViewOwl.

.DESCRIPTION
    ViewOwl uses a specific Chrome for Testing version to render HTML templates.
    This script downloads and extracts the binary to the correct location so that
    'dotnet run' can find it automatically.

    Pinned version: 143.0.7499.169
    Target folder:  ViewOwl.Grabber.Engine\Chrome-for-testing\Chrome-win64\

.NOTES
    Run once from the solution root before starting the server for the first time.
    Re-run only when upgrading Chrome (bump $ChromeVersion here and in Chrome.cs).
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Pinned version — change here AND in Chrome.cs when upgrading ──────────────
$ChromeVersion = "143.0.7499.169"

$Url    = "https://storage.googleapis.com/chrome-for-testing-public/$ChromeVersion/win64/chrome-win64.zip"
$Zip    = "$env:TEMP\viewowl-chrome-win64.zip"
$Target = Join-Path $PSScriptRoot "ViewOwl.Grabber.Engine\Chrome-for-testing"
$Exe    = Join-Path $Target "Chrome-win64\chrome.exe"

if (Test-Path $Exe) {
    Write-Host "Chrome for Testing $ChromeVersion already present at:" -ForegroundColor Green
    Write-Host "  $Exe" -ForegroundColor Green
    Write-Host "Nothing to do. Run 'dotnet run --project ViewOwl.Grabber.WebAPI' to start the server."
    exit 0
}

Write-Host "Downloading Chrome for Testing $ChromeVersion ..." -ForegroundColor Cyan
Write-Host "  Source: $Url"
Invoke-WebRequest -Uri $Url -OutFile $Zip -UseBasicParsing

Write-Host "Extracting to $Target ..."
if (-not (Test-Path $Target)) {
    New-Item -ItemType Directory -Path $Target | Out-Null
}
Expand-Archive -Path $Zip -DestinationPath $Target -Force
Remove-Item $Zip

if (Test-Path $Exe) {
    Write-Host ""
    Write-Host "Done. Chrome for Testing $ChromeVersion is ready." -ForegroundColor Green
    Write-Host "Run 'dotnet run --project ViewOwl.Grabber.WebAPI' to start the server."
} else {
    Write-Host ""
    Write-Host "ERROR: chrome.exe not found after extraction. Expected:" -ForegroundColor Red
    Write-Host "  $Exe" -ForegroundColor Red
    Write-Host "Check the archive structure and adjust the Target path if needed."
    exit 1
}
