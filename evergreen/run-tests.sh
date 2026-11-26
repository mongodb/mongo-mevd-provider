#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

# TEST_SUITE selects what to run:
#   unit         - test/MongoDB.UnitTests only; needs no MongoDB.
#   conformance  - unit tests plus test/MongoDB.ConformanceTests, which boots an Atlas Local container through
#                  Testcontainers, so needs Docker. MongoDB:ConnectionURL must not be set (see MongoTestEnvironment).
TEST_SUITE=${TEST_SUITE:=unit}
BUILD_CONFIGURATION=${BUILD_CONFIGURATION:=Release}

results_dir="./artifacts/test-results"
logger_args=(--logger "junit;LogFileName=TEST_{assembly}.xml;FailureBodyFormat=Verbose" --logger "console;verbosity=normal")

echo "Building solution ('${BUILD_CONFIGURATION}')"
dotnet clean ./MongoDB.VectorData.slnx -c "${BUILD_CONFIGURATION}"
dotnet build ./MongoDB.VectorData.slnx -c "${BUILD_CONFIGURATION}"

echo "Running unit tests"
dotnet test ./test/MongoDB.UnitTests/MongoDB.UnitTests.csproj -c "${BUILD_CONFIGURATION}" --no-build \
  --results-directory "${results_dir}/unit" "${logger_args[@]}"

if [ "$TEST_SUITE" = "conformance" ]; then
  echo "Running conformance tests"
  # Evergreen hosts are ephemeral, so the Testcontainers resource reaper (an extra image pull) isn't needed.
  TESTCONTAINERS_RYUK_DISABLED=true \
    dotnet test ./test/MongoDB.ConformanceTests/MongoDB.ConformanceTests.csproj -c "${BUILD_CONFIGURATION}" --no-build \
    --framework net10.0 --results-directory "${results_dir}/conformance" "${logger_args[@]}"
fi
