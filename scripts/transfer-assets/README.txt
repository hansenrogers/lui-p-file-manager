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

Uninstall
- Double-click Uninstall.cmd

Alternative PowerShell uninstall
- .\Uninstall-LuiPFileManager.ps1 -ForceCloseRunningApp

Optional uninstall switches
- -RemoveAppData
- -KeepShellIntegration
