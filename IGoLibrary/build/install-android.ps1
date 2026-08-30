param(
    [string]$ApkPath,
    [string]$AndroidSdkDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$workspaceRoot = Split-Path -Parent $root

if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $ApkPath = Join-Path $root "artifacts\android\IGoLibrary-Android.apk"
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    $AndroidSdkDirectory = Join-Path $workspaceRoot ".tools\android-sdk"
}

$adb = Join-Path $AndroidSdkDirectory "platform-tools\adb.exe"
if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) {
    throw "adb was not found at $adb"
}

if (-not (Test-Path -LiteralPath $ApkPath -PathType Leaf)) {
    throw "APK was not found at $ApkPath. Run build\publish-android.ps1 first."
}

$devicesOutput = & $adb devices
if ($LASTEXITCODE -ne 0) {
    throw "adb devices failed with exit code $LASTEXITCODE."
}

$connectedDevices = $devicesOutput |
    Select-Object -Skip 1 |
    Where-Object { $_ -match "\sdevice$" }

if (-not $connectedDevices) {
    throw "No Android device or emulator is connected. Enable USB debugging, connect the phone, and run this script again."
}

& $adb install -r $ApkPath
if ($LASTEXITCODE -ne 0) {
    throw "adb install failed with exit code $LASTEXITCODE."
}

Write-Host "Installed Android APK from $ApkPath"
