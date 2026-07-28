param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProject = Join-Path $repoRoot 'LuiP.FileManager.App\LuiP.FileManager.App.csproj'
$distRoot = Join-Path $repoRoot 'dist'
$bundleRoot = Join-Path $distRoot 'LuiPFileManager-Installer'
$appBundleDir = Join-Path $bundleRoot 'app'
$zipPath = Join-Path $distRoot 'LuiPFileManager-Installer.zip'
$publishDir = Join-Path $repoRoot "LuiP.FileManager.App\bin\$Configuration\net8.0-windows\$Runtime\publish-selfcontained"

if ($Clean.IsPresent -and (Test-Path $distRoot)) {
    Remove-Item -Path $distRoot -Recurse -Force
}

if (Test-Path $bundleRoot) {
    Remove-Item -Path $bundleRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Write-Host 'Publishing self-contained app payload...'
& dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=false /p:PublishDir="$publishDir\"

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

if (-not (Test-Path (Join-Path $publishDir 'LuiP.FileManager.App.exe'))) {
    throw "Publish output missing executable at: $publishDir"
}

New-Item -Path $bundleRoot -ItemType Directory -Force | Out-Null
New-Item -Path $appBundleDir -ItemType Directory -Force | Out-Null

Copy-Item -Path (Join-Path $publishDir '*') -Destination $appBundleDir -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'transfer-assets\Install-LuiPFileManager.ps1') -Destination (Join-Path $bundleRoot 'Install-LuiPFileManager.ps1') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'transfer-assets\Uninstall-LuiPFileManager.ps1') -Destination (Join-Path $bundleRoot 'Uninstall-LuiPFileManager.ps1') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'transfer-assets\Install.cmd') -Destination (Join-Path $bundleRoot 'Install.cmd') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'transfer-assets\Uninstall.cmd') -Destination (Join-Path $bundleRoot 'Uninstall.cmd') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'transfer-assets\README.txt') -Destination (Join-Path $bundleRoot 'README.txt') -Force

Write-Host 'Creating zip package...'
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Transfer installer created successfully.'
Write-Host "Folder: $bundleRoot"
Write-Host "Zip:    $zipPath"
Write-Host ''
Write-Host 'On target computer:'
Write-Host '1) Extract zip'
Write-Host '2) Double-click Install.cmd (recommended)'
Write-Host '   or run .\Install-LuiPFileManager.ps1 -CreateDesktopShortcut -LaunchAfterInstall'
