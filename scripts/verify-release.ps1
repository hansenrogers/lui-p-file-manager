param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath,

    [Parameter(Mandatory = $true)]
    [string]$ChecksumFilePath
)

if (-not (Test-Path $ArchivePath)) {
    Write-Error "Archive not found: $ArchivePath"
    exit 1
}

if (-not (Test-Path $ChecksumFilePath)) {
    Write-Error "Checksum file not found: $ChecksumFilePath"
    exit 1
}

$hash = Get-FileHash -Path $ArchivePath -Algorithm SHA256
$expectedLine = Get-Content -Path $ChecksumFilePath -TotalCount 1

if ($expectedLine -notmatch '([A-Fa-f0-9]{64})$') {
    Write-Error "Checksum file format is invalid."
    exit 1
}

$expected = $Matches[1].ToUpperInvariant()
$actual = $hash.Hash.ToUpperInvariant()

if ($actual -eq $expected) {
    Write-Host "Checksum OK"
    Write-Host "File: $ArchivePath"
    Write-Host "SHA256: $actual"
    exit 0
}

Write-Error "Checksum mismatch. Expected $expected but found $actual"
exit 2
