#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

if [ -z "$PACKAGE_VERSION" ]; then
  PACKAGE_VERSION=$(bash ./evergreen/generate-version.sh)
  echo Calculated PACKAGE_VERSION value: "$PACKAGE_VERSION"
fi

echo Creating nuget package "$PACKAGE_VERSION" using Release build configuration...

dotnet clean ./MongoDB.VectorData.slnx -c Release
dotnet pack ./src/MongoDB/MongoDB.csproj -o ./artifacts/nuget -c Release -p:Version="$PACKAGE_VERSION" --include-symbols -p:SymbolPackageFormat=snupkg -p:ContinuousIntegrationBuild=true
