#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactDirectory
)

$ErrorActionPreference = "Stop"
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
$ChecksumPath = Join-Path $ArtifactDirectory "SHA256SUMS.txt"
if (-not (Test-Path $ChecksumPath -PathType Leaf)) {
    throw "SHA256SUMS.txt is missing."
}

$ChecksummedNames = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($Line in Get-Content $ChecksumPath) {
    if ($Line -notmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Invalid checksum line: $Line"
    }
    $Name = $Matches[2]
    if ([IO.Path]::IsPathRooted($Name) -or
        $Name -ne [IO.Path]::GetFileName($Name)) {
        throw "Checksum entry must contain only a file name: $Name"
    }
    $Path = Join-Path $ArtifactDirectory $Name
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Checksummed artifact is missing: $($Matches[2])"
    }
    $Actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($Actual -ne $Matches[1]) {
        throw "Checksum mismatch: $($Matches[2])"
    }
    if (-not $ChecksummedNames.Add($Matches[2])) {
        throw "Duplicate checksum entry: $($Matches[2])"
    }
}
foreach ($Artifact in Get-ChildItem $ArtifactDirectory -File) {
    if ($Artifact.Name -ne "SHA256SUMS.txt" -and
        -not $ChecksummedNames.Contains($Artifact.Name)) {
        throw "Artifact is not covered by SHA256SUMS.txt: $($Artifact.Name)"
    }
}

function Get-SingleArtifact {
    param([Parameter(Mandatory)][string]$Filter)
    $Items = @(Get-ChildItem $ArtifactDirectory -File -Filter $Filter)
    if ($Items.Count -ne 1) {
        throw "Expected exactly one '$Filter' artifact, found $($Items.Count)."
    }
    return $Items[0]
}

$Portable = Get-SingleArtifact "*-win-x64.zip"
$Installer = Get-SingleArtifact "*-Setup-*-x64.exe"
$SbomPath = Get-SingleArtifact "*.spdx.json"
$ProjectSource = Get-SingleArtifact "*-project-source.zip"

Add-Type -AssemblyName System.IO.Compression.FileSystem
$Archive = [IO.Compression.ZipFile]::OpenRead($Portable.FullName)
try {
    $Names = @($Archive.Entries.FullName.Replace("\", "/"))
    if (-not ($Names -match '/mpvnet\.exe$')) {
        throw "Portable archive does not contain mpvnet.exe."
    }
    if (-not ($Names -match '/portable_config/extensions/CensorExtension/CensorExtension\.dll$')) {
        throw "Portable archive does not contain CensorExtension.dll."
    }
    if ($Names -match '/Censor\.Core\.dll$|/CensorExtension\.deps\.json$') {
        throw "Portable archive contains a forbidden legacy extension file."
    }
    if (-not ($Names -match '/VERSION\.json$') -or
        -not ($Names -match '/THIRD_PARTY_NOTICES\.md$') -or
        -not ($Names -match '/LICENSE$')) {
        throw "Portable archive is missing version or license metadata."
    }
}
finally {
    $Archive.Dispose()
}

$SourceArchive = [IO.Compression.ZipFile]::OpenRead($ProjectSource.FullName)
try {
    $SourceNames = @($SourceArchive.Entries.FullName.Replace("\", "/"))
    foreach ($RequiredSource in @(".gitattributes", "CensorPlayer.sln", "LICENSE")) {
        if ($SourceNames -notcontains $RequiredSource) {
            throw "Project source archive is missing $RequiredSource."
        }
    }
}
finally {
    $SourceArchive.Dispose()
}

$Sbom = Get-Content $SbomPath.FullName -Raw | ConvertFrom-Json
if ($Sbom.spdxVersion -ne "SPDX-2.3" -or
    $Sbom.dataLicense -ne "CC0-1.0" -or
    $Sbom.files.Count -lt 1 -or
    $Sbom.packages.name -notcontains "mpv.net" -or
    $Sbom.packages.name -notcontains "mpv" -or
    $Sbom.packages.name -notcontains "FFmpeg") {
    throw "SPDX SBOM is invalid or empty."
}
$PrimaryPackage = $Sbom.packages |
    Where-Object SPDXID -eq "SPDXRef-Package-CensorPlayer" |
    Select-Object -First 1
if ($null -eq $PrimaryPackage -or
    $PrimaryPackage.filesAnalyzed -ne $true -or
    @($PrimaryPackage.licenseInfoFromFiles | Where-Object { $_ }).Count -lt 1) {
    throw "SPDX primary package analysis metadata is incomplete."
}
foreach ($File in $Sbom.files) {
    if (@($File.licenseInfoInFiles | Where-Object { $_ }).Count -lt 1) {
        throw "SPDX file license metadata is incomplete: $($File.fileName)"
    }
}

$PayloadArchive = [IO.Compression.ZipFile]::OpenRead($Portable.FullName)
try {
    $PayloadFiles = @($PayloadArchive.Entries | Where-Object { $_.Name })
    $RootEntry = $PayloadFiles |
        Where-Object { $_.FullName.Replace("\", "/") -match '/VERSION\.json$' } |
        Select-Object -First 1
    if ($null -eq $RootEntry) {
        throw "Unable to identify the portable archive root."
    }
    $Root = $RootEntry.FullName.Substring(
        0,
        $RootEntry.FullName.Length - "VERSION.json".Length)
    if ($PayloadFiles.Count -ne $Sbom.files.Count) {
        throw "SPDX file count does not match the portable payload."
    }
    foreach ($File in $Sbom.files) {
        $Relative = $File.fileName -replace '^\./', ''
        $Entry = $PayloadArchive.GetEntry(($Root + $Relative).Replace("/", "\"))
        if ($null -eq $Entry) {
            $Entry = $PayloadArchive.GetEntry(($Root + $Relative).Replace("\", "/"))
        }
        if ($null -eq $Entry) {
            throw "SPDX file is missing from the portable payload: $Relative"
        }
        $Expected = $File.checksums |
            Where-Object algorithm -eq "SHA256" |
            Select-Object -ExpandProperty checksumValue -First 1
        $Stream = $Entry.Open()
        try {
            $Actual = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($Stream)
            ).ToLowerInvariant()
        }
        finally {
            $Stream.Dispose()
        }
        if ($Actual -ne $Expected) {
            throw "SPDX checksum mismatch: $Relative"
        }
    }
}
finally {
    $PayloadArchive.Dispose()
}

Write-Host "Release artifact verification passed."
