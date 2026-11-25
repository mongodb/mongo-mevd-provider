#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

# The SDK version comes from global.json so that CI and local builds use the same one. The test projects run on
# net10.0, which the SDK provides; the net8.0 and netstandard2.1 library targets only need targeting packs, which
# restore downloads. net472 is built (not run) on Windows only.

# DOTNET_SDK_PATH is set by Evergreen (outside the checkout, so MSBuild globbing can't pick it up); run from the repo root.
DOTNET_SDK_PATH=${DOTNET_SDK_PATH:=../.dotnet}
mkdir -p "$DOTNET_SDK_PATH"

if [[ $OS =~ [Ww]indows.* ]]; then
  # From .NET 11 the installer prefers .tar.gz on Windows whenever a `tar` is on PATH. On Evergreen that is
  # GNU tar, which treats `C:\...` as a remote host ("Cannot connect to C: resolve failed"), so force .zip.
  export DOTNET_INSTALL_SKIP_TAR=1
  echo "Downloading Windows .NET SDK installer into $DOTNET_SDK_PATH folder..."
  curl -Lfo "$DOTNET_SDK_PATH"/dotnet-install.ps1 https://dot.net/v1/dotnet-install.ps1
  echo "Installing .NET SDK from global.json..."
  powershell.exe "$DOTNET_SDK_PATH"/dotnet-install.ps1 -JSonFile ./global.json -InstallDir "$DOTNET_SDK_PATH" -NoPath
else
  echo "Downloading .NET SDK installer into $DOTNET_SDK_PATH folder..."
  curl -Lfo "$DOTNET_SDK_PATH"/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
  echo "Installing .NET SDK from global.json..."
  bash "$DOTNET_SDK_PATH"/dotnet-install.sh --jsonfile ./global.json --install-dir "$DOTNET_SDK_PATH" --no-path
fi
