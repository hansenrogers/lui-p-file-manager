# Local Install Scripts

These scripts install and remove a local non-admin deployment under `%LOCALAPPDATA%\\LuiPFileManager`.

## Prerequisite

Publish the app first:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" publish .\LuiP.FileManager.App\LuiP.FileManager.App.csproj -c Release -r win-x64 --self-contained false
```

## Install

```powershell
.\scripts\install-local.ps1
```

Optional switches:

- `-CreateDesktopShortcut`: also creates a desktop shortcut.
- `-ForceCloseRunningApp`: force-closes a running app before replacing files.
- `-PublishDir <path>`: override publish folder.

Example:

```powershell
.\scripts\install-local.ps1 -CreateDesktopShortcut -ForceCloseRunningApp
```

## Uninstall

```powershell
.\scripts\uninstall-local.ps1
```

Optional switches:

- `-ForceCloseRunningApp`: force-closes a running app before removal.
- `-KeepShellIntegration`: keep Explorer shell key untouched.
- `-RemoveAppData`: remove entire `%LOCALAPPDATA%\\LuiPFileManager` tree.

Example:

```powershell
.\scripts\uninstall-local.ps1 -ForceCloseRunningApp -RemoveAppData
```

## Shell Integration Note

These scripts install the app files and shortcuts. Explorer integration is managed inside the app from **Settings > Windows Explorer Integration**.

## In-App Uninstall Shortcut

When running from a workspace/development layout that includes this scripts folder, you can launch the same uninstall flow from **Settings > Run Local Uninstall...**.
