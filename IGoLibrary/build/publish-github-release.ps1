param(
    [string]$Version = "1.0.17",
    [string]$Repo = "Luofaiz/IGoLibrary",
    [string]$Notes = "Initial release."
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$installerPath = Join-Path $root "artifacts\installer\IGoLibrarySetup.exe"
$manifestPath = Join-Path $root "artifacts\installer\latest.json"
$zipPath = Join-Path $root "artifacts\windows\win-x64\IGoLibrary-Windows-x64.zip"

foreach ($path in @($installerPath, $manifestPath, $zipPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release asset was not found: $path"
    }
}

$tag = "v$Version"
$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $existingRelease = gh release view $tag --repo $Repo --json tagName --jq ".tagName" 2>$null
    $releaseViewExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($releaseViewExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingRelease)) {
    gh release upload $tag $installerPath $manifestPath $zipPath --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub release asset upload failed."
    }
    Write-Host "Updated release assets for $Repo $tag"
    exit 0
}

gh release create $tag $installerPath $manifestPath $zipPath `
    --repo $Repo `
    --title "IGoLibrary $tag" `
    --notes $Notes

if ($LASTEXITCODE -ne 0) {
    throw "GitHub release creation failed."
}

Write-Host "Created GitHub release $Repo $tag"
