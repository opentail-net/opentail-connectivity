# Contributing

## Set up

The .NET SDK version is pinned in `global.json` (10.0.x). Then:

```
dotnet restore OpenTail.Connectivity.slnx
dotnet build OpenTail.Connectivity.slnx
dotnet test --solution OpenTail.Connectivity.slnx
```

Tests use xunit v3 on Microsoft.Testing.Platform, hence `--solution`. CI runs the same build and test on Ubuntu and Windows, so
paths and line endings need to work on both.

## House rules

- **Warnings are errors** (`TreatWarningsAsErrors`, `AnalysisLevel` latest). A build with warnings fails.
- **Package versions live in `Directory.Packages.props`** (central package management). Add a `PackageVersion` there and a
  versionless `PackageReference` in the project. The restore audit is on: a new vulnerability advisory fails the build until the
  package is upgraded or pinned, as the existing transitive pin does.
- **Dependencies stay on public NuGet packages**, and new ones need a reason.
- **Read-only by default.** A new source connector must not write to its source. Anything that sends, edits or deletes needs an
  explicit gate and a note in [docs/safety-and-credentials.md](docs/safety-and-credentials.md).
- **Allow-lists over wildcards** for fields and columns, as in the Csv and Sql connectors.
- **Never commit credentials**, and keep them out of logs and exception messages (see `CredentialRedactor`).

## Adding a connector

See [docs/writing-a-connector.md](docs/writing-a-connector.md). New packages follow the existing layout: a project named
`OpenTail.Connectivity.<Name>` with a `Description`, a reference to `OpenTail.Connectivity.Files`, an entry in
`OpenTail.Connectivity.slnx`, tests in `OpenTail.Connectivity.Tests`, and a row in the README table and
[docs/connectors.md](docs/connectors.md).

## Versions and releases

All packages share one version, derived by MinVer from `v*` tags. Do not edit version numbers in project files. Releases are made
by maintainers by pushing a `v*` tag, which makes CI publish the packages; do not push tags from a branch or pull request.
