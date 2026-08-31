param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "1.0.17",
    [switch]$SelfContained = $true,
    [string]$PackageName
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "src\IGoLibrary.Desktop\IGoLibrary.Desktop.csproj"
$publishRoot = Join-Path $root "artifacts\publish"
$output = Join-Path $publishRoot $Runtime
$packageOutput = Join-Path $root "artifacts\windows\$Runtime"
$executableName = "IGoLibrary.exe"
if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $runtimeLabel = $Runtime -replace "^win-", ""
    $PackageName = "IGoLibrary-Windows-$runtimeLabel.zip"
}
$zipPath = Join-Path $packageOutput $PackageName

if (Test-Path -LiteralPath $output) {
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    $resolvedPublishRoot = (Resolve-Path -LiteralPath $publishRoot).Path.TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $publishRootPrefix = $resolvedPublishRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedOutput.StartsWith($publishRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected publish output path: $resolvedOutput"
    }

    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained:$SelfContained `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:UsedAvaloniaProducts= `
    -p:UseSharedCompilation=false `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path $output $executableName
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Published executable was not found: $publishedExecutable"
}

New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Published desktop app to $output"
Write-Host "Created Windows zip at $zipPath"
Write-Host "To build the installer, open build\\IGoLibrary.iss in Inno Setup and compile it."
