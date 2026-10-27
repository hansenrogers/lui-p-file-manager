Lui-P File Manager - Transfer Installer

This folder installs Lui-P File Manager for the current user without admin rights.

Files
- Install.cmd
- Uninstall.cmd
- Install-LuiPFileManager.ps1
- Uninstall-LuiPFileManager.ps1
- app\ (application payload)

Install on another computer
1. Copy this whole folder (or the zip produced by create-transfer-installer.ps1) to the target computer.
2. Double-click Install.cmd (recommended).

Alternative PowerShell install
- .\Install-LuiPFileManager.ps1 -CreateDesktopShortcut -LaunchAfterInstall

Optional install switches
- -ForceCloseRunningApp
- -SkipWebView2Install

Notes
- Installer checks for Microsoft Edge WebView2 Runtime and installs it if missing.
- Explorer integration can be enabled from Settings inside the app.

Public download link
- This is the client-download process:
	1. Keep the release tied to a pushed `v*` tag.
	2. Make the repository public when you want the download to work without login.
	3. Share the release asset URL directly.
	4. Test the link in a private/incognito browser session or with `curl`.
- Example: https://github.com/hansenrogers/lui-p-file-manager/releases/download/v1.0.2/LuiP.FileManager-win-x64.zip
- If the link says 404, the release is not public yet or the version tag does not match.

Uninstall
- Double-click Uninstall.cmd

Alternative PowerShell uninstall
- .\Uninstall-LuiPFileManager.ps1 -ForceCloseRunningApp

Optional uninstall switches
- -RemoveAppData
- -KeepShellIntegration
