param(
    [string]$Configuration = "Release",
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$workspaceRoot = Split-Path -Parent $root
$project = Join-Path $root "src\IGoLibrary.Android\IGoLibrary.Android.csproj"
$artifactRoot = Join-Path $root "artifacts\android"
$dotnet = Join-Path $workspaceRoot ".tools\dotnet\dotnet.exe"

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    $dotnet = "dotnet"
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    $AndroidSdkDirectory = Join-Path $workspaceRoot ".tools\android-sdk"
}

if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
    $JavaSdkDirectory = Join-Path $workspaceRoot ".tools\jdk"
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

& $dotnet publish $project `
    -c $Configuration `
    -f net10.0-android `
    -p:AndroidPackageFormat=apk `
    -p:AndroidSdkDirectory=$AndroidSdkDirectory `
    -p:JavaSdkDirectory=$JavaSdkDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Android publish failed with exit code $LASTEXITCODE."
}

$publishDirectory = Join-Path $root "src\IGoLibrary.Android\bin\$Configuration\net10.0-android\publish"
$apk = Get-ChildItem -LiteralPath $publishDirectory -Filter "*.apk" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $apk) {
    throw "Published APK was not found in $publishDirectory"
}

$targetApk = Join-Path $artifactRoot "IGoLibrary-Android.apk"
Copy-Item -LiteralPath $apk.FullName -Destination $targetApk -Force

$buildToolsRoot = Join-Path $AndroidSdkDirectory "build-tools"
$latestBuildTools = $null
if (Test-Path -LiteralPath $buildToolsRoot -PathType Container) {
    $latestBuildTools = Get-ChildItem -LiteralPath $buildToolsRoot -Directory |
        Sort-Object Name -Descending |
        Select-Object -First 1
}

if ($null -ne $latestBuildTools) {
    $zipalign = Join-Path $latestBuildTools.FullName "zipalign.exe"
    if (Test-Path -LiteralPath $zipalign -PathType Leaf) {
        & $zipalign -c -p 4 $targetApk
        if ($LASTEXITCODE -ne 0) {
            throw "Android APK zipalign verification failed with exit code $LASTEXITCODE."
        }
    }

    $apksigner = Join-Path $latestBuildTools.FullName "apksigner.bat"
    if (Test-Path -LiteralPath $apksigner -PathType Leaf) {
        $previousJavaHome = $env:JAVA_HOME
        $previousPath = $env:PATH
        try {
            if (Test-Path -LiteralPath $JavaSdkDirectory -PathType Container) {
                $env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaSdkDirectory).Path
                $env:PATH = "$env:JAVA_HOME\bin;$env:PATH"
            }

            & $apksigner verify --verbose $targetApk
            if ($LASTEXITCODE -ne 0) {
                throw "Android APK signature verification failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            $env:JAVA_HOME = $previousJavaHome
            $env:PATH = $previousPath
        }
    }
}

Write-Host "Created Android APK at $targetApk"
