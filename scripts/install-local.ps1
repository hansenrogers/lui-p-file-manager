param(
    [string]$PublishDir = "$(Join-Path $PSScriptRoot '..\LuiP.FileManager.App\bin\Release\net8.0-windows\win-x64\publish')",
    [switch]$CreateDesktopShortcut,
    [switch]$ForceCloseRunningApp
)

$ErrorActionPreference = 'Stop'

$resolvedPublishDir = (Resolve-Path $PublishDir).Path
if (-not (Test-Path $resolvedPublishDir)) {
    throw "Publish directory not found: $PublishDir"
}

$installRoot = Join-Path $env:LOCALAPPDATA 'LuiPFileManager'
$appDir = Join-Path $installRoot 'app'

$running = Get-Process -Name 'LuiP.FileManager.App' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $ForceCloseRunningApp.IsPresent) {
        throw "Lui-P File Manager is currently running. Close it first, or rerun with -ForceCloseRunningApp."
    }

    $running | Stop-Process -Force -ErrorAction Stop
}

if (-not (Test-Path $installRoot)) {
    New-Item -Path $installRoot -ItemType Directory -Force | Out-Null
}

if (Test-Path $appDir) {
    Remove-Item -Path $appDir -Recurse -Force
}

New-Item -Path $appDir -ItemType Directory -Force | Out-Null

$copySource = Join-Path $resolvedPublishDir '*'
Copy-Item -Path $copySource -Destination $appDir -Recurse -Force

$exePath = Join-Path $appDir 'LuiP.FileManager.App.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected executable was not found at $exePath"
}

$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lui-P File Manager'
if (-not (Test-Path $startMenuFolder)) {
    New-Item -Path $startMenuFolder -ItemType Directory -Force | Out-Null
}

$startMenuShortcut = Join-Path $startMenuFolder 'Lui-P File Manager.lnk'
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($startMenuShortcut)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $appDir
$shortcut.IconLocation = "$exePath,0"
$shortcut.Save()

if ($CreateDesktopShortcut.IsPresent) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lui-P File Manager.lnk'
    $desktop = $wsh.CreateShortcut($desktopShortcut)
    $desktop.TargetPath = $exePath
    $desktop.WorkingDirectory = $appDir
    $desktop.IconLocation = "$exePath,0"
    $desktop.Save()
}

Write-Host "Install complete"
Write-Host "Executable: $exePath"
Write-Host "Start menu shortcut: $startMenuShortcut"
Write-Host "Next step: open app Settings and click Install under Windows Explorer Integration."
Write-Host "Tip: use scripts/uninstall-local.ps1 to remove this local install cleanly."
