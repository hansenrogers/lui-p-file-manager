param(
    [switch]$RemoveAppData,
    [switch]$KeepShellIntegration,
    [switch]$ForceCloseRunningApp
)

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'LuiPFileManager'
$appDir = Join-Path $installRoot 'app'

$running = Get-Process -Name 'LuiP.FileManager.App' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $ForceCloseRunningApp.IsPresent) {
        throw "Lui-P File Manager is currently running. Close it first, or rerun with -ForceCloseRunningApp."
    }

    $running | Stop-Process -Force -ErrorAction Stop
}

$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lui-P File Manager'
$startMenuShortcut = Join-Path $startMenuFolder 'Lui-P File Manager.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lui-P File Manager.lnk'

if (Test-Path $startMenuShortcut) {
    Remove-Item -Path $startMenuShortcut -Force
}

if (Test-Path $startMenuFolder) {
    Remove-Item -Path $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $desktopShortcut) {
    Remove-Item -Path $desktopShortcut -Force
}

if (Test-Path $appDir) {
    Remove-Item -Path $appDir -Recurse -Force
}

if ($RemoveAppData.IsPresent -and (Test-Path $installRoot)) {
    Remove-Item -Path $installRoot -Recurse -Force
}

# Cleanup per-user Explorer integration if present.
if (-not $KeepShellIntegration.IsPresent) {
    $root = 'HKCU:\Software\Classes\AllFilesystemObjects\shell\LuiPFileManagerCopy'
    if (Test-Path $root) {
        Remove-Item -Path $root -Recurse -Force
    }
}

Write-Host 'Uninstall complete'
if ($KeepShellIntegration.IsPresent) {
    Write-Host 'Explorer integration key was preserved by request.'
}
else {
    Write-Host 'Explorer integration key removed (if it existed).'
}
