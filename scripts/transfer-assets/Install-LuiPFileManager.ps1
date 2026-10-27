param(
    [switch]$CreateDesktopShortcut,
    [switch]$ForceCloseRunningApp,
    [switch]$SkipWebView2Install,
    [switch]$LaunchAfterInstall
)

$ErrorActionPreference = 'Stop'

function Test-WebView2RuntimeInstalled {
    $clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $paths = @(
        "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$clientId",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
        "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\$clientId"
    )

    foreach ($path in $paths) {
        if (Test-Path $path) {
            $version = (Get-ItemProperty -Path $path -ErrorAction SilentlyContinue).pv
            if (-not [string]::IsNullOrWhiteSpace($version)) {
                return $true
            }
        }
    }

    return $false
}

function Ensure-WebView2Runtime {
    if (Test-WebView2RuntimeInstalled) {
        Write-Host 'WebView2 Runtime: already installed.'
        return
    }

    Write-Host 'WebView2 Runtime: not found. Installing Evergreen bootstrapper...'
    $bootstrapperUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
    $tempExe = Join-Path $env:TEMP 'MicrosoftEdgeWebView2Setup.exe'

    try {
        Invoke-WebRequest -Uri $bootstrapperUrl -OutFile $tempExe -UseBasicParsing
        $proc = Start-Process -FilePath $tempExe -ArgumentList '/silent', '/install' -Wait -PassThru
        if ($proc.ExitCode -ne 0 -and $proc.ExitCode -ne 2147748096) {
            throw "WebView2 installer exited with code $($proc.ExitCode)."
        }
    }
    finally {
        if (Test-Path $tempExe) {
            Remove-Item -Path $tempExe -Force -ErrorAction SilentlyContinue
        }
    }

    if (-not (Test-WebView2RuntimeInstalled)) {
        throw 'WebView2 Runtime installation did not complete successfully.'
    }

    Write-Host 'WebView2 Runtime installed.'
}

$packageRoot = $PSScriptRoot
$appPayloadDir = Join-Path $packageRoot 'app'
$payloadExe = Join-Path $appPayloadDir 'LuiP.FileManager.App.exe'

if (-not (Test-Path $appPayloadDir) -or -not (Test-Path $payloadExe)) {
    throw "App payload not found. Expected: $payloadExe"
}

if (-not $SkipWebView2Install.IsPresent) {
    Ensure-WebView2Runtime
}
else {
    Write-Host 'Skipping WebView2 runtime install by request.'
}

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

if (-not (Test-Path $installRoot)) {
    New-Item -Path $installRoot -ItemType Directory -Force | Out-Null
}

if (Test-Path $appDir) {
    Remove-Item -Path $appDir -Recurse -Force
}

New-Item -Path $appDir -ItemType Directory -Force | Out-Null
Copy-Item -Path (Join-Path $appPayloadDir '*') -Destination $appDir -Recurse -Force

$exePath = Join-Path $appDir 'LuiP.FileManager.App.exe'
if (-not (Test-Path $exePath)) {
    throw "Expected executable was not found at $exePath"
}

$iconSource = Join-Path $packageRoot 'LuiPFileManager.ico'
$iconPath = Join-Path $appDir 'LuiPFileManager.ico'
if (Test-Path $iconSource) {
    Copy-Item -Path $iconSource -Destination $iconPath -Force
}

$iconLocation = if (Test-Path $iconPath) { "$iconPath,0" } else { "$exePath,0" }

Remove-AppShortcuts

$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Lui-P File Manager'
if (-not (Test-Path $startMenuFolder)) {
    New-Item -Path $startMenuFolder -ItemType Directory -Force | Out-Null
}

$startMenuShortcut = Join-Path $startMenuFolder 'Lui-P File Manager.lnk'
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($startMenuShortcut)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $appDir
$shortcut.IconLocation = $iconLocation
$shortcut.Save()

if ($CreateDesktopShortcut.IsPresent) {
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Lui-P File Manager.lnk'
    $desktop = $wsh.CreateShortcut($desktopShortcut)
    $desktop.TargetPath = $exePath
    $desktop.WorkingDirectory = $appDir
    $desktop.IconLocation = $iconLocation
    $desktop.Save()
}

Write-Host 'Install complete.'
Write-Host "Executable: $exePath"
Write-Host "Start menu shortcut: $startMenuShortcut"
Write-Host 'Optional next step: open app Settings and click Install under Windows Explorer Integration.'
Write-Host "To uninstall, run: .\Uninstall-LuiPFileManager.ps1"

if ($LaunchAfterInstall.IsPresent) {
    try {
        Start-Process -FilePath $exePath -WorkingDirectory $appDir -ErrorAction Stop
    }
    catch {
        Write-Warning "Install succeeded, but automatic app launch failed: $($_.Exception.Message)"
        Write-Warning 'You can start Lui-P File Manager from the Start menu shortcut.'
    }
}
