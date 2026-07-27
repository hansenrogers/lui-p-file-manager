# Lui-P File Manager

[![CI](https://github.com/hansenrogers/lui-p-file-manager/actions/workflows/ci.yml/badge.svg)](https://github.com/hansenrogers/lui-p-file-manager/actions/workflows/ci.yml)
[![Release](https://github.com/hansenrogers/lui-p-file-manager/actions/workflows/release.yml/badge.svg)](https://github.com/hansenrogers/lui-p-file-manager/actions/workflows/release.yml)

A Windows WPF file management tool with indexing, operation tracking, and supporting infrastructure/services.

## Solution

- `LuiP.FileManager.App`: WPF desktop application
- `LuiP.FileManager.Core`: core models and service interfaces
- `LuiP.FileManager.Infrastructure`: persistence and platform service implementations
- `LuiP.FileManager.Tests`: unit tests

## Local Development

Prerequisites:

- .NET SDK 8.0+
- Windows (for WPF app build/run)

Commands:

```powershell
dotnet restore LuiPFileManager.sln
dotnet build LuiPFileManager.sln
dotnet test LuiP.FileManager.Tests/LuiP.FileManager.Tests.csproj
```

## CI

The CI workflow runs on each push and pull request to `main` and performs:

- restore
- Release build
- test execution

## Release Automation

The release workflow runs when pushing a tag matching `v*` (example: `v1.0.0`) or via manual dispatch with a `tag` input. It:

- builds and publishes the app for `win-x64`
- zips publish output
- generates a SHA256 checksum file for the zip
- generates a release changelog from git history
- creates a GitHub release
- uploads the zip, checksum, and changelog artifacts

### Verify Downloaded Artifact

After downloading a release zip and `.sha256` file, verify integrity:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-release.ps1 -ArchivePath .\LuiP.FileManager-win-x64.zip -ChecksumFilePath .\LuiP.FileManager-win-x64.zip.sha256
```
