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

Optional: set an explicit release version for the output name:

```powershell
.\scripts\create-transfer-installer.ps1 -Clean -ReleaseVersion v1.0.2
```

Output:

- `dist/LuiPFileManager-Installer-<version>/`
- `dist/LuiPFileManager-Installer-<version>.zip`

When `-ReleaseVersion` is not provided, the script uses the current git tag (or latest tag) and falls back to a timestamped `dev-...` value.

On the target computer:

1. Extract `LuiPFileManager-Installer.zip`
2. Run `Install-LuiPFileManager.ps1 -CreateDesktopShortcut -LaunchAfterInstall`

Dependency behavior:

- The installer checks for Microsoft Edge WebView2 Runtime.
- If missing, it downloads and installs WebView2 automatically.

## Public Download Link

This is the standard workflow for client downloads:

1. Make the repository public if the asset must be downloadable without login.
2. Publish the release from a pushed `v*` tag.
3. Share the release asset URL directly.
4. Verify the URL outside your signed-in browser session if you need to confirm public access.

To send a client a public download link, use the GitHub release asset URL after the release has been published from a pushed `v*` tag:

```text
https://github.com/hansenrogers/lui-p-file-manager/releases/download/v1.0.2/LuiP.FileManager-win-x64.zip
```

If that link returns 404, the release is not public yet or the tag/version does not match the published release.

Checksum link:

```text
https://github.com/hansenrogers/lui-p-file-manager/releases/download/v1.0.2/LuiP.FileManager-win-x64.zip.sha256
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
