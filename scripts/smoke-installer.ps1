#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath
)

$ErrorActionPreference = "Stop"
if ($env:GITHUB_ACTIONS -ne "true") {
    throw "Installer smoke is CI-only because it deletes its test install and data."
}

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
$ExtensionDirectory = Join-Path $PortableConfig "extensions/CensorExtension"
$InstalledDll = Join-Path $ExtensionDirectory "CensorExtension.dll"
$LegacyCore = Join-Path $ExtensionDirectory "Censor.Core.dll"
$LegacyDeps = Join-Path $ExtensionDirectory "CensorExtension.deps.json"

if ((Test-Path $InstallRoot) -or (Test-Path $DataRoot)) {
    throw "Installer smoke refuses to overwrite an existing CensorPlayer install or data directory."
}

New-Item -ItemType Directory -Force $DataRoot | Out-Null
"retain me" | Set-Content $Marker

$Setup = Start-Process $InstallerPath `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=`"$InstallRoot`"", "/LOG=`"$SetupLog`"" `
    -Wait -PassThru
if ($Setup.ExitCode -ne 0) {
    throw "Installer failed with exit code $($Setup.ExitCode)."
}
if (-not (Test-Path (Join-Path $InstallRoot "mpvnet.exe") -PathType Leaf) -or
    -not (Test-Path $InstalledDll -PathType Leaf)) {
    throw "Installed payload is incomplete."
}
$ExpectedDllHash = (Get-FileHash $InstalledDll -Algorithm SHA256).Hash
Add-Content $InputConfig "# installer-update-preservation-smoke"
"runtime state" | Set-Content $RuntimeState
[IO.File]::WriteAllBytes($InstalledDll, [byte[]](1..32))
"legacy" | Set-Content $LegacyCore
"legacy" | Set-Content $LegacyDeps

# A second silent install must replace the extension and preserve the live config.
$Update = Start-Process $InstallerPath `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=`"$InstallRoot`"", "/LOG=`"$SetupLog`"" `
    -Wait -PassThru
if ($Update.ExitCode -ne 0 -or
    -not (Test-Path $Marker -PathType Leaf) -or
    -not (Test-Path $RuntimeState -PathType Leaf) -or
    -not (Test-Path $InstalledDll -PathType Leaf) -or
    (Get-FileHash $InstalledDll -Algorithm SHA256).Hash -ne $ExpectedDllHash -or
    (Test-Path $LegacyCore) -or
    (Test-Path $LegacyDeps) -or
    -not ((Get-Content $InputConfig -Raw).Contains(
        "# installer-update-preservation-smoke",
        [StringComparison]::Ordinal))) {
    throw "Update did not replace the extension cleanly or removed user data."
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
while (((Test-Path (Join-Path $InstallRoot "mpvnet.exe")) -or
        (Test-Path $InstalledDll)) -and
       [DateTime]::UtcNow -lt $UninstallDeadline) {
    Start-Sleep -Milliseconds 200
}
if ((Test-Path (Join-Path $InstallRoot "mpvnet.exe")) -or
    (Test-Path $InstalledDll)) {
    throw "Program payload remained after uninstall."
}
if (-not (Test-Path $Marker -PathType Leaf) -or
    -not (Test-Path $InputConfig -PathType Leaf) -or
    -not (Test-Path $RuntimeState -PathType Leaf)) {
    throw "Uninstall removed user data without explicit opt-in."
}

$Reinstall = Start-Process $InstallerPath `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=`"$InstallRoot`"", "/LOG=`"$SetupLog`"" `
    -Wait -PassThru
if ($Reinstall.ExitCode -ne 0 -or
    -not (Test-Path $InstalledDll -PathType Leaf) -or
    -not (Test-Path $Marker -PathType Leaf)) {
    throw "Reinstall before opt-in cleanup failed."
}

$OptInUninstaller = Join-Path $InstallRoot "unins000.exe"
$OptInUninstall = Start-Process $OptInUninstaller `
    -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DELETEUSERDATA=1", "/LOG=`"$UninstallLog`"" `
    -Wait -PassThru
if ($OptInUninstall.ExitCode -ne 0) {
    throw "Opt-in uninstall failed with exit code $($OptInUninstall.ExitCode)."
}
$CleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
while (((Test-Path $PortableConfig) -or (Test-Path $DataRoot)) -and
       [DateTime]::UtcNow -lt $CleanupDeadline) {
    Start-Sleep -Milliseconds 200
}
if ((Test-Path $PortableConfig) -or (Test-Path $DataRoot)) {
    throw "Opt-in uninstall did not remove all user data."
}

Write-Host "Installer install/update/default-retention/opt-in-cleanup smoke passed."
