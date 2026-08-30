param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.0",
    [string]$RepoOwner = "Luofaiz",
    [string]$RepoName = "IGoLibrary",
    [string]$Notes = "Initial release."
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeLabel = $Runtime -replace "^win-", ""
$windowsPackageName = "IGoLibrary-Windows-$runtimeLabel.zip"

$localDotnet = Join-Path (Split-Path -Parent $root) ".tools\dotnet\dotnet.exe"
if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

& (Join-Path $root "build\publish-windows.ps1") `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Version $Version `
    -PackageName $windowsPackageName

if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $iscc) {
    $candidatePaths = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )
    $isccPath = $candidatePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
} else {
    $isccPath = $iscc.Source
}

if ([string]::IsNullOrWhiteSpace($isccPath)) {
    throw "Inno Setup 6 compiler was not found. Install it with: winget install --id JRSoftware.InnoSetup -e"
}

& $isccPath "/DMyAppVersion=$Version" (Join-Path $root "build\IGoLibrary.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $root "artifacts\installer\IGoLibrarySetup.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer was not found: $installerPath"
}

& (Join-Path $root "build\create-update-manifest.ps1") `
    -InstallerPath $installerPath `
    -Version $Version `
    -RepoOwner $RepoOwner `
    -RepoName $RepoName `
    -Notes $Notes `
    -OutputPath (Join-Path $root "artifacts\installer\latest.json")

Write-Host "Created installer at $installerPath"
Write-Host "Created release zip at $(Join-Path $root "artifacts\windows\$Runtime\$windowsPackageName")"
