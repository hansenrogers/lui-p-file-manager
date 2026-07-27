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

The release workflow runs when pushing a tag matching `v*` (example: `v1.0.0`) or via manual dispatch. It:

- builds and publishes the app for `win-x64`
- zips publish output
- creates a GitHub release
- uploads the zip artifact to that release
