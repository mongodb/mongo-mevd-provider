#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

# Runs the conformance tests against the packed (and signed) MongoDB.VectorData package instead of the project
# reference, to check the package that will be published actually works. Needs Docker, as run-tests.sh does.

PROVIDER_PROJECT="./src/MongoDB/MongoDB.csproj"
TESTS_PROJECT="./test/MongoDB.ConformanceTests/MongoDB.ConformanceTests.csproj"

if [ -z "$PACKAGE_VERSION" ]; then
  echo "PACKAGE_VERSION variable should be set"
  exit 1
fi

# Restore from copies rather than `git checkout`, so uncommitted local edits aren't lost.
backup_dir=$(mktemp -d)
cp "$TESTS_PROJECT" "$backup_dir/tests.csproj"
cp ./Directory.Packages.props "$backup_dir/Directory.Packages.props"

restore_changes() {
  echo "Reverting changes made for the smoke tests"
  cp "$backup_dir/tests.csproj" "$TESTS_PROJECT"
  cp "$backup_dir/Directory.Packages.props" ./Directory.Packages.props
  rm -f ./nuget.config
  rm -rf "$backup_dir"
}
trap restore_changes EXIT

echo "Retargeting conformance tests to use the generated package instead of the project reference..."
# Keep nuget.org available alongside the local source for the other dependencies.
cat > ./nuget.config <<'EOT'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOT
dotnet nuget add source "./artifacts/nuget" -n local --configfile "./nuget.config"
dotnet nuget locals temp -c
dotnet clean ./MongoDB.VectorData.slnx

dotnet remove "$TESTS_PROJECT" reference "$PROVIDER_PROJECT"
dotnet add "$TESTS_PROJECT" package "MongoDB.VectorData" -v "$PACKAGE_VERSION"

echo "Run tests: $TESTS_PROJECT"
TESTCONTAINERS_RYUK_DISABLED=true \
  dotnet test "$TESTS_PROJECT" -c Release --framework net10.0 \
  --results-directory "./artifacts/test-results/smoke" \
  --logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=normal"
