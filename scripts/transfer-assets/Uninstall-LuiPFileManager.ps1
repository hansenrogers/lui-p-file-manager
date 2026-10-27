param(
    [switch]$RemoveAppData,
    [switch]$KeepShellIntegration,
    [switch]$ForceCloseRunningApp
)

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:LOCALAPPDATA 'LuiPFileManager'
$appDir = Join-Path $installRoot 'app'

function Remove-AppShortcuts {
    $shortcutRoots = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        ([Environment]::GetFolderPath('Desktop')),
        (Join-Path $env:PUBLIC 'Desktop')
    )

    foreach ($root in $shortcutRoots) {
        if ([string]::IsNullOrWhiteSpace($root) -or -not (Test-Path $root)) {
            continue
        }

        Get-ChildItem -Path $root -Filter 'Lui-P File Manager*.lnk' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

$running = Get-Process -Name 'LuiP.FileManager.App' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $ForceCloseRunningApp.IsPresent) {
        throw 'Lui-P File Manager is currently running. Close it first, or rerun with -ForceCloseRunningApp.'
    }

    $running | Stop-Process -Force -ErrorAction Stop
}

$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lui-P File Manager'
$startMenuShortcut = Join-Path $startMenuFolder 'Lui-P File Manager.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lui-P File Manager.lnk'

Remove-AppShortcuts

if (Test-Path $startMenuFolder) {
    Remove-Item -Path $startMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
}

if (Test-Path $appDir) {
    Remove-Item -Path $appDir -Recurse -Force
}

if ($RemoveAppData.IsPresent -and (Test-Path $installRoot)) {
    Remove-Item -Path $installRoot -Recurse -Force
}

if (-not $KeepShellIntegration.IsPresent) {
    $root = 'HKCU:\Software\Classes\AllFilesystemObjects\shell\LuiPFileManagerCopy'
    if (Test-Path $root) {
        Remove-Item -Path $root -Recurse -Force
    }
}

Write-Host 'Uninstall complete.'
if ($KeepShellIntegration.IsPresent) {
    Write-Host 'Explorer integration key was preserved by request.'
}
else {
    Write-Host 'Explorer integration key removed (if it existed).'
}
