param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$Version = "1.0.0",
    [string]$RepoOwner = "Luofaiz",
    [string]$RepoName = "IGoLibrary",
    [string]$Notes = "Initial release.",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$installerItem = Get-Item -LiteralPath $InstallerPath
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $installerItem.DirectoryName "latest.json"
}

$sha256 = (Get-FileHash -LiteralPath $installerItem.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$downloadUrl = "https://github.com/$RepoOwner/$RepoName/releases/latest/download/$($installerItem.Name)"
$releaseUrl = "https://github.com/$RepoOwner/$RepoName/releases/latest"

$manifest = [ordered]@{
    version = $Version
    notes = $Notes
    downloadUrl = $downloadUrl
    downloadSha256 = $sha256
    releaseUrl = $releaseUrl
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

Write-Host "Created update manifest at $OutputPath"
Write-Host "Installer SHA256: $sha256"
