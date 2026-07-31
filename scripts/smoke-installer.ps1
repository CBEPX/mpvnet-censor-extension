#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$InstallRoot = Join-Path $env:LOCALAPPDATA "Programs/CensorPlayer"
$DataRoot = Join-Path $env:LOCALAPPDATA "CensorPlayer"
$Marker = Join-Path $DataRoot "installer-retention-smoke.txt"
$TempRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$SetupLog = Join-Path $TempRoot "censorplayer-setup.log"
$UninstallLog = Join-Path $TempRoot "censorplayer-uninstall.log"
$PortableConfig = Join-Path $InstallRoot "portable_config"
$InputConfig = Join-Path $PortableConfig "input.conf"
$RuntimeState = Join-Path $PortableConfig "installer-runtime-state.txt"

New-Item -ItemType Directory -Force $DataRoot | Out-Null
"retain me" | Set-Content $Marker

$Setup = Start-Process $InstallerPath `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$SetupLog`"" `
    -Wait -PassThru
if ($Setup.ExitCode -ne 0) {
    throw "Installer failed with exit code $($Setup.ExitCode)."
}
if (-not (Test-Path (Join-Path $InstallRoot "mpvnet.exe") -PathType Leaf) -or
    -not (Test-Path (
        Join-Path $InstallRoot "portable_config/extensions/CensorExtension/CensorExtension.dll"
    ) -PathType Leaf)) {
    throw "Installed payload is incomplete."
}
Add-Content $InputConfig "# installer-update-preservation-smoke"
"runtime state" | Set-Content $RuntimeState

# A second silent install must preserve extension data and the live portable config.
$Update = Start-Process $InstallerPath `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$SetupLog`"" `
    -Wait -PassThru
if ($Update.ExitCode -ne 0 -or
    -not (Test-Path $Marker -PathType Leaf) -or
    -not (Test-Path $RuntimeState -PathType Leaf) -or
    -not ((Get-Content $InputConfig -Raw).Contains(
        "# installer-update-preservation-smoke",
        [StringComparison]::Ordinal))) {
    throw "Update install failed or removed user data."
}

$Uninstaller = Join-Path $InstallRoot "unins000.exe"
if (-not (Test-Path $Uninstaller -PathType Leaf)) {
    throw "Uninstaller is missing."
}
$Uninstall = Start-Process $Uninstaller `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=`"$UninstallLog`"" `
    -Wait -PassThru
if ($Uninstall.ExitCode -ne 0) {
    throw "Uninstaller failed with exit code $($Uninstall.ExitCode)."
}
$UninstallDeadline = [DateTime]::UtcNow.AddSeconds(15)
while ((Test-Path $InstallRoot) -and [DateTime]::UtcNow -lt $UninstallDeadline) {
    Start-Sleep -Milliseconds 200
}
if (Test-Path $InstallRoot) {
    throw "Program payload remained after uninstall."
}
if (-not (Test-Path $Marker -PathType Leaf)) {
    throw "Uninstall removed user data without explicit opt-in."
}
Remove-Item $Marker -Force

Write-Host "Installer install/update/uninstall smoke passed."
