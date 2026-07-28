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

## Build Transfer Installer (Other Computers)

Create a copyable installer bundle and zip (self-contained app payload plus dependency-aware install scripts):

```powershell
.\scripts\create-transfer-installer.ps1 -Clean
```

Output:

- `dist/LuiPFileManager-Installer/`
- `dist/LuiPFileManager-Installer.zip`

On the target computer:

1. Extract `LuiPFileManager-Installer.zip`
2. Run `Install-LuiPFileManager.ps1 -CreateDesktopShortcut -LaunchAfterInstall`

Dependency behavior:

- The installer checks for Microsoft Edge WebView2 Runtime.
- If missing, it downloads and installs WebView2 automatically.

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
