#requires -Version 7.0

[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Commit = "",
    [string]$RunId = "",
    [string]$OutputDirectory = "artifacts/windows",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$Lock = Get-Content (Join-Path $RepoRoot "deps.lock.json") -Raw | ConvertFrom-Json
$OutputRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory))
$ArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot "artifacts"))
$ArtifactsPrefix = $ArtifactsRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
) + [IO.Path]::DirectorySeparatorChar
$DownloadRoot = Join-Path $RepoRoot ".deps/downloads"
$ExtensionDll = Join-Path $RepoRoot "src/Censor.MpvNet.Extension/bin/Release/CensorExtension.dll"

if (-not (Test-Path $ExtensionDll -PathType Leaf)) {
    throw "Release extension DLL is missing: $ExtensionDll"
}
$DllProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExtensionDll).ProductVersion
$DllReleaseVersion = ($DllProductVersion -split "\+", 2)[0]
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $DllReleaseVersion
}
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,63}$') {
    throw "Version contains unsupported characters."
}
if ($DllReleaseVersion -ne $Version) {
    throw "Release version $Version does not match CensorExtension.dll version $DllProductVersion."
}
$StageParent = Join-Path $OutputRoot "stage"
$StageRoot = Join-Path $StageParent "CensorPlayer-$Version-win-x64"
$ArtifactRoot = Join-Path $OutputRoot "release"

if (-not $OutputRoot.StartsWith(
    $ArtifactsPrefix,
    [StringComparison]::OrdinalIgnoreCase
)) {
    throw "OutputDirectory must resolve below $ArtifactsRoot."
}
if ([string]::IsNullOrWhiteSpace($Commit)) {
    $Commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
}
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Commit must be an exact 40-character Git SHA."
}
if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { "local" }
}

function Get-LockedDownload {
    param(
        [Parameter(Mandatory)]$Entry,
        [Parameter(Mandatory)][string]$FileName
    )
    New-Item -ItemType Directory -Force $DownloadRoot | Out-Null
    $Path = Join-Path $DownloadRoot $FileName
    if (Test-Path $Path -PathType Leaf) {
        $Actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Actual -ne $Entry.sha256) {
            Remove-Item $Path -Force
        }
    }
    if (-not (Test-Path $Path -PathType Leaf)) {
        Invoke-WebRequest `
            -Uri $Entry.url `
            -OutFile $Path `
            -MaximumRetryCount 3 `
            -RetryIntervalSec 2
    }
    $Actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $Entry.sha256) {
        throw "SHA-256 mismatch for $FileName. Expected $($Entry.sha256), got $Actual."
    }
    if ($Entry.PSObject.Properties.Name -contains "size" -and
        (Get-Item $Path).Length -ne $Entry.size) {
        throw "Size mismatch for $FileName. Expected $($Entry.size) bytes."
    }
    return $Path
}

function Install-PinnedInnoSetup {
    $Entry = $Lock.tools.innoSetup
    $Installer = Get-LockedDownload `
        -Entry $Entry `
        -FileName "innosetup-$($Entry.version).exe"
    $ToolRoot = Join-Path $RepoRoot ".deps/tools/inno-setup-$($Entry.version)"
    $Iscc = Join-Path $ToolRoot "ISCC.exe"
    $Marker = Join-Path $ToolRoot ".installer-sha256"
    $Ready = (Test-Path $Iscc -PathType Leaf) -and
        (Test-Path $Marker -PathType Leaf) -and
        ((Get-Content $Marker -Raw).Trim() -eq $Entry.sha256)
    if (-not $Ready) {
        if (Test-Path $ToolRoot) {
            Remove-Item $ToolRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force $ToolRoot | Out-Null
        $Install = Start-Process $Installer `
            -ArgumentList @(
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/CURRENTUSER",
                "/DIR=`"$ToolRoot`""
            ) `
            -Wait -PassThru
        if ($Install.ExitCode -ne 0 -or -not (Test-Path $Iscc -PathType Leaf)) {
            throw "Pinned Inno Setup installation failed with exit code $($Install.ExitCode)."
        }
        $Entry.sha256 | Set-Content $Marker -Encoding ascii
    }
    return $Iscc
}

function Add-SpdxFile {
    param(
        [Parameter(Mandatory)]$File,
        [Parameter(Mandatory)][int]$Index
    )
    $Relative = [IO.Path]::GetRelativePath($StageRoot, $File.FullName).Replace("\", "/")
    return [ordered]@{
        fileName = "./$Relative"
        SPDXID = "SPDXRef-File-$Index"
        checksums = @(
            [ordered]@{
                algorithm = "SHA256"
                checksumValue = (Get-FileHash $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            },
            [ordered]@{
                algorithm = "SHA1"
                checksumValue = (Get-FileHash $File.FullName -Algorithm SHA1).Hash.ToLowerInvariant()
            }
        )
        licenseConcluded = "NOASSERTION"
        copyrightText = "NOASSERTION"
    }
}

if (Test-Path $OutputRoot) {
    Remove-Item $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Force $StageRoot, $ArtifactRoot | Out-Null

$PortableZip = Get-LockedDownload `
    -Entry $Lock.mpvNet.portableX64 `
    -FileName "mpv.net-$($Lock.mpvNet.version)-portable-x64.zip"
Expand-Archive -Path $PortableZip -DestinationPath $StageRoot

$PortableConfig = Join-Path $StageRoot "portable_config"
$ExtensionDirectory = Join-Path $PortableConfig "extensions/CensorExtension"
$LicensesDirectory = Join-Path $StageRoot "LICENSES"
New-Item -ItemType Directory -Force $ExtensionDirectory, $LicensesDirectory | Out-Null
Copy-Item $ExtensionDll (Join-Path $ExtensionDirectory "CensorExtension.dll")
Copy-Item (Join-Path $RepoRoot "examples/input.conf") (Join-Path $PortableConfig "input.conf")
Copy-Item (Join-Path $RepoRoot "packaging/windows-portable/mpv.conf") (Join-Path $PortableConfig "mpv.conf")
Copy-Item (Join-Path $RepoRoot "packaging/windows-portable/mpvnet.conf") (Join-Path $PortableConfig "mpvnet.conf")
Copy-Item (Join-Path $RepoRoot "LICENSE") (Join-Path $StageRoot "LICENSE")
Copy-Item (Join-Path $RepoRoot "THIRD_PARTY_NOTICES.md") (Join-Path $StageRoot "THIRD_PARTY_NOTICES.md")
Copy-Item (Join-Path $RepoRoot "LICENSE") (Join-Path $LicensesDirectory "CensorPlayer-GPL-2.0-only.txt")
Copy-Item (Join-Path $RepoRoot ".deps/mpvnet-source/License.txt") `
    (Join-Path $LicensesDirectory "mpv.net-GPL-2.0.txt")

$VersionManifest = [ordered]@{
    schema = 1
    product = "CensorPlayer"
    version = $Version
    commit = $Commit
    workflowRunId = $RunId
    architecture = "x64"
    mpvNetVersion = $Lock.mpvNet.version
    mpvNetCommit = $Lock.mpvNet.sourceCommit
    createdAtUtc = [DateTime]::UtcNow.ToString("O")
}
$VersionManifest | ConvertTo-Json -Depth 5 |
    Set-Content (Join-Path $StageRoot "VERSION.json") -Encoding utf8NoBOM

$Forbidden = @("Censor.Core.dll", "CensorExtension.deps.json")
foreach ($Name in $Forbidden) {
    if (Get-ChildItem $StageRoot -Recurse -File -Filter $Name) {
        throw "Forbidden legacy extension file found: $Name"
    }
}
if (-not (Test-Path (Join-Path $StageRoot "mpvnet.exe") -PathType Leaf)) {
    throw "mpvnet.exe is missing from staged payload."
}
if (-not (Test-Path (Join-Path $ExtensionDirectory "CensorExtension.dll") -PathType Leaf)) {
    throw "CensorExtension.dll is missing from staged payload."
}

$ProjectSource = Join-Path $ArtifactRoot "CensorPlayer-$Version-project-source.zip"
& git -C $RepoRoot archive --format=zip --output=$ProjectSource $Commit
if ($LASTEXITCODE -ne 0) {
    throw "Unable to archive project source at $Commit."
}

$PackageZip = Join-Path $ArtifactRoot "CensorPlayer-$Version-win-x64.zip"
Compress-Archive -Path $StageRoot -DestinationPath $PackageZip

$Files = Get-ChildItem $StageRoot -Recurse -File
$SpdxFiles = for ($Index = 0; $Index -lt $Files.Count; $Index++) {
    Add-SpdxFile -File $Files[$Index] -Index ($Index + 1)
}
$Sha1List = $Files |
    ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA1).Hash.ToLowerInvariant() } |
    Sort-Object
$VerificationBytes = [Text.Encoding]::UTF8.GetBytes(($Sha1List -join ""))
$VerificationCode = [Convert]::ToHexString(
    [Security.Cryptography.SHA1]::HashData($VerificationBytes)
).ToLowerInvariant()
$Relationships = @(
    [ordered]@{
        spdxElementId = "SPDXRef-DOCUMENT"
        relationshipType = "DESCRIBES"
        relatedSpdxElement = "SPDXRef-Package-CensorPlayer"
    },
    [ordered]@{
        spdxElementId = "SPDXRef-Package-CensorPlayer"
        relationshipType = "CONTAINS"
        relatedSpdxElement = "SPDXRef-Package-mpvnet"
    },
    [ordered]@{
        spdxElementId = "SPDXRef-Package-CensorPlayer"
        relationshipType = "CONTAINS"
        relatedSpdxElement = "SPDXRef-Package-mpv"
    },
    [ordered]@{
        spdxElementId = "SPDXRef-Package-CensorPlayer"
        relationshipType = "CONTAINS"
        relatedSpdxElement = "SPDXRef-Package-FFmpeg"
    }
)
for ($Index = 0; $Index -lt $Files.Count; $Index++) {
    $Relationships += [ordered]@{
        spdxElementId = "SPDXRef-Package-CensorPlayer"
        relationshipType = "CONTAINS"
        relatedSpdxElement = "SPDXRef-File-$($Index + 1)"
    }
}
$Sbom = [ordered]@{
    spdxVersion = "SPDX-2.3"
    dataLicense = "CC0-1.0"
    SPDXID = "SPDXRef-DOCUMENT"
    name = "CensorPlayer-$Version-win-x64"
    documentNamespace = "https://github.com/CBEPX/mpvnet-censor-extension/releases/$Version/$Commit"
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        creators = @("Tool: scripts/package-windows.ps1")
    }
    packages = @(
        [ordered]@{
            name = "CensorPlayer"
            SPDXID = "SPDXRef-Package-CensorPlayer"
            versionInfo = $Version
            downloadLocation = "NOASSERTION"
            filesAnalyzed = $true
            packageVerificationCode = [ordered]@{
                packageVerificationCodeValue = $VerificationCode
            }
            licenseConcluded = "GPL-2.0-only"
            licenseDeclared = "GPL-2.0-only"
            copyrightText = "NOASSERTION"
        },
        [ordered]@{
            name = "mpv.net"
            SPDXID = "SPDXRef-Package-mpvnet"
            versionInfo = $Lock.mpvNet.version
            downloadLocation = $Lock.mpvNet.portableX64.url
            filesAnalyzed = $false
            licenseConcluded = "GPL-2.0-only"
            licenseDeclared = "GPL-2.0-only"
            copyrightText = "NOASSERTION"
        },
        [ordered]@{
            name = "mpv"
            SPDXID = "SPDXRef-Package-mpv"
            versionInfo = $Lock.runtimeSources.mpv.commit
            downloadLocation = $Lock.runtimeSources.mpv.url
            filesAnalyzed = $false
            licenseConcluded = "NOASSERTION"
            licenseDeclared = "GPL-2.0-or-later"
            copyrightText = "NOASSERTION"
        },
        [ordered]@{
            name = "FFmpeg"
            SPDXID = "SPDXRef-Package-FFmpeg"
            versionInfo = $Lock.runtimeSources.ffmpeg.commit
            downloadLocation = $Lock.runtimeSources.ffmpeg.url
            filesAnalyzed = $false
            licenseConcluded = "NOASSERTION"
            licenseDeclared = "NOASSERTION"
            copyrightText = "NOASSERTION"
        }
    )
    files = @($SpdxFiles)
    relationships = $Relationships
}
$SbomPath = Join-Path $ArtifactRoot "CensorPlayer-$Version.spdx.json"
$Sbom | ConvertTo-Json -Depth 10 | Set-Content $SbomPath -Encoding utf8NoBOM

if (-not $SkipInstaller) {
    $Iscc = Install-PinnedInnoSetup
    & $Iscc `
        "/DAppVersion=$Version" `
        "/DStageDir=$StageRoot" `
        "/DArtifactDir=$ArtifactRoot" `
        (Join-Path $RepoRoot "packaging/windows-installer/CensorPlayer.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
}

$ChecksummedFiles = Get-ChildItem $ArtifactRoot -File |
    Where-Object Name -ne "SHA256SUMS.txt" |
    Sort-Object Name
$ChecksumLines = foreach ($File in $ChecksummedFiles) {
    "{0}  {1}" -f `
        (Get-FileHash $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), `
        $File.Name
}
$ChecksumLines | Set-Content (Join-Path $ArtifactRoot "SHA256SUMS.txt") -Encoding ascii

Write-Host "Staged payload: $StageRoot"
Write-Host "Release artifacts: $ArtifactRoot"
