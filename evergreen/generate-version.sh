#!/usr/bin/env bash
set -o errexit  # Exit the script with error if any of the commands fail

# Release builds are triggered by a `v<version>` git tag (e.g. v1.0.0-preview.1) and use that version.
# Other builds use `git describe` against the latest `v*` tag; if the repo has no such tag yet, fall back to
# the <Version> in the project file plus a CI suffix so that the package version is still valid SemVer.

if [ -n "$triggered_by_git_tag" ]; then
    PACKAGE_VERSION=${triggered_by_git_tag#v}
elif PACKAGE_VERSION=$(git describe --tags --match 'v*' 2>/dev/null); then
    PACKAGE_VERSION=${PACKAGE_VERSION#v}
else
    BASE_VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' ./src/MongoDB/MongoDB.csproj | head -n 1)
    PACKAGE_VERSION="${BASE_VERSION}-ci.$(git rev-parse --short HEAD)"
fi

echo "$PACKAGE_VERSION"
