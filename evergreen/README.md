# Evergreen CI and release

`evergreen.yml` is the Evergreen project configuration (set the project's config file to `evergreen/evergreen.yml`).
It is modelled on the MongoDB EF Core provider's setup.

## What runs

| Build variant | Tasks | Runs on |
|---|---|---|
| Windows unit tests | Builds every target framework (including `net472`) and runs the unit tests | every commit / patch |
| Ubuntu 22.04 unit and conformance tests | Unit tests, then the conformance suite against an Atlas Local container (Docker + Testcontainers) | every commit / patch |
| Package Pack | `dotnet pack`, Authenticode-sign, upload to S3 | every commit / patch |
| Smoke tests | Conformance suite re-run against the packed `MongoDB.VectorData` package instead of the project reference | git tag only |
| Package Push (nuget) | Verify signature, push to nuget.org | git tag only, after pack and smoke tests |
| Package Push (myget) | Push to MyGet | manual, any commit |
| SSDLC Reports | Papertrail trace, augmented SBOM, SSDLC compliance report | git tag only, after nuget push |

Assemblies are **not** strong-named. Only the NuGet package is signed (Authenticode, via Garasign).

## Versioning and releasing

The package version comes from the git tag. To release `1.0.0-preview.1`, push the tag `v1.0.0-preview.1`; the
`release_tag` variants then run. For other builds `generate-version.sh` uses `git describe` against the latest `v*`
tag, or, before any tag exists, `<Version>` from `src/MongoDB/MongoDB.csproj` plus `-ci.<short sha>`. The version is
passed to `dotnet pack` as `-p:Version`.

## Running the scripts locally

```bash
TEST_SUITE=unit bash evergreen/run-tests.sh          # unit tests only
TEST_SUITE=conformance bash evergreen/run-tests.sh   # plus conformance tests (needs Docker)
PACKAGE_VERSION=$(bash evergreen/generate-version.sh) bash evergreen/pack-packages.sh
PACKAGE_VERSION=... bash evergreen/run-smoke-tests.sh   # needs the packed package in ./artifacts/nuget
```

Leave `MongoDB__ConnectionURL` unset so the conformance tests start their own Atlas Local container.

## Evergreen project settings

Project variables (Project Settings > Variables), all as in the EF Core provider project:

| Variable | Used for |
|---|---|
| `aws_key`, `aws_secret`, `aws_upload_bucket` | `s3.put` / `s3.get` of packages between tasks |
| `ecr-evergreen-role` | Role to pull the Garasign and silkbomb images from ECR |
| `GRS_USERNAME`, `GRS_PASSWORD`, `AUTHENTICODE_KEY_NAME` | Garasign signing |
| `NUGET_SIGN_CERTIFICATE_FINGERPRINT` | Signature verification before push; SSDLC report |
| `nuget_api_key`, `myget_api_key` | Package push |
| `papertrail_key_id`, `papertrail_secret_key` | Papertrail trace |
| `kondukto_role_arn`, `UPLOAD_SSDLC_RELEASE_ASSETS_ROLE_ARN` | SBOM augmentation and SSDLC report upload |

Git tag versions: enable "Git Tag Versions" for the project, allow the tag-triggering users/teams, and add a git tag
alias with tag regex `v.*` that selects variants tagged `release_tag` and all tasks (`.*`).

