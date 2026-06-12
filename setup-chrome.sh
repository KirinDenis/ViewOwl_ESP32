#!/usr/bin/env bash
# setup-chrome.sh — install system Chromium for ViewOwl (Linux / macOS)
#
# On Linux the server uses the system 'chromium-browser' package automatically
# (no manual path configuration needed). Run this script once before starting
# the server for the first time.
#
# On Windows use setup-chrome.ps1 instead.

set -euo pipefail

echo "Setting up Chromium for ViewOwl..."

if command -v apt-get &>/dev/null; then
    # Debian / Ubuntu / Raspberry Pi OS
    sudo apt-get update -qq
    sudo apt-get install -y chromium-browser
    echo ""
    echo "Done. Run 'dotnet run --project ViewOwl.Grabber.WebAPI' to start the server."

elif command -v brew &>/dev/null; then
    # macOS (Homebrew)
    brew install --cask chromium
    echo ""
    echo "NOTE: macOS is not an officially supported deployment target."
    echo "      The server will attempt to use /Applications/Chromium.app/Contents/MacOS/Chromium."
    echo "      You may need to adjust Chrome.cs → GetChromiumExecutablePath() if the path differs."
    echo ""
    echo "Done. Run 'dotnet run --project ViewOwl.Grabber.WebAPI' to start the server."

else
    echo "ERROR: Neither apt-get nor brew found."
    echo "Please install Chromium manually and ensure it is available at /usr/bin/chromium-browser."
    exit 1
fi
