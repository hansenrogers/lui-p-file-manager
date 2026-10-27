param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$ReleaseVersion,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

function Resolve-ReleaseVersion {
    param(
        [string]$ExplicitVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        return $ExplicitVersion.Trim()
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_REF_NAME) -and $env:GITHUB_REF_NAME -match '^v') {
        return $env:GITHUB_REF_NAME.Trim()
    }

    $exactTag = (& git describe --tags --exact-match 2>$null)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($exactTag)) {
        return $exactTag.Trim()
    }

    $latestTag = (& git tag --sort=-creatordate | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($latestTag)) {
        return $latestTag.Trim()
    }

    return "dev-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}

function Convert-ToSafeFileNameSegment {
    param(
        [string]$Value
    )

    $safe = $Value.Trim()
    foreach ($char in [System.IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace($char, '-')
    }

    $safe = $safe.Replace(' ', '-')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return "dev-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    }

    return $safe
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProject = Join-Path $repoRoot 'LuiP.FileManager.App\LuiP.FileManager.App.csproj'
$distRoot = Join-Path $repoRoot 'dist'
$resolvedVersion = Resolve-ReleaseVersion -ExplicitVersion $ReleaseVersion
$safeVersion = Convert-ToSafeFileNameSegment -Value $resolvedVersion
$bundleName = "LuiPFileManager-Installer-$safeVersion"
$bundleRoot = Join-Path $distRoot $bundleName
$appBundleDir = Join-Path $bundleRoot 'app'
$zipPath = Join-Path $distRoot "$bundleName.zip"
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

$iconAssetPath = Join-Path $repoRoot 'LuiP.FileManager.App\Assets\LuiPFileManager.ico'
if (Test-Path $iconAssetPath) {
    Copy-Item -Path $iconAssetPath -Destination (Join-Path $bundleRoot 'LuiPFileManager.ico') -Force
}

Write-Host 'Creating zip package...'
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Transfer installer created successfully.'
Write-Host "Version: $resolvedVersion"
Write-Host "Folder: $bundleRoot"
Write-Host "Zip:    $zipPath"
Write-Host ''
Write-Host 'On target computer:'
Write-Host '1) Extract zip'
Write-Host '2) Double-click Install.cmd (recommended)'
Write-Host '   or run .\Install-LuiPFileManager.ps1 -CreateDesktopShortcut -LaunchAfterInstall'
