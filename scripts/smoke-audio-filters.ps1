#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MpvNetPath,
    [string]$ExtensionAssemblyPath
)

$ErrorActionPreference = "Stop"
$MpvNetPath = [IO.Path]::GetFullPath($MpvNetPath)
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ExtensionAssemblyPath = if ([string]::IsNullOrWhiteSpace($ExtensionAssemblyPath)) {
    [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MpvNetPath) "portable_config/extensions/CensorExtension/CensorExtension.dll"))
} elseif ([IO.Path]::IsPathRooted($ExtensionAssemblyPath)) {
    [IO.Path]::GetFullPath($ExtensionAssemblyPath)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $ExtensionAssemblyPath))
}
if (-not (Test-Path $MpvNetPath -PathType Leaf)) {
    throw "mpv.net console launcher is missing: $MpvNetPath"
}
if (-not (Test-Path $ExtensionAssemblyPath -PathType Leaf)) {
    throw "CensorExtension assembly is missing: $ExtensionAssemblyPath"
}

$PresetJson = & dotnet run `
    --project (Join-Path $RepoRoot "tests/Censor.LoaderSmoke/Censor.LoaderSmoke.csproj") `
    --configuration Release `
    --no-build `
    --no-restore `
    -- `
    --audio-presets `
    $ExtensionAssemblyPath
if ($LASTEXITCODE -ne 0) {
    throw "Could not read audio presets from CensorExtension.dll."
}
$Presets = $PresetJson | ConvertFrom-Json
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("censor-audio-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $TempRoot | Out-Null

try {
    foreach ($Preset in $Presets) {
        $StdOut = Join-Path $TempRoot "$($Preset.Id).stdout.log"
        $StdErr = Join-Path $TempRoot "$($Preset.Id).stderr.log"
        $Process = Start-Process $MpvNetPath `
            -ArgumentList @(
                "--no-config",
                # Empty output selects mpv.net's deterministic headless event loop.
                "--o=",
                "--load-scripts=no",
                "--input-terminal=no",
                "--idle=no",
                "--keep-open=no",
                "--video=no",
                "--ao=null",
                "--msg-level=all=warn",
                "--af-add=$($Preset.Filter)",
                "av://lavfi:anullsrc=r=48000:cl=stereo:d=2"
            ) `
            -RedirectStandardOutput $StdOut `
            -RedirectStandardError $StdErr `
            -PassThru
        if (-not $Process.WaitForExit(30000)) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit()
            $TimeoutOutput = (
                Get-Content $StdOut, $StdErr -Raw -ErrorAction SilentlyContinue
            ) -join "`n"
            throw "Audio filter smoke timed out for preset $($Preset.Id):`n$TimeoutOutput"
        }
        $Output = (Get-Content $StdOut, $StdErr -Raw -ErrorAction SilentlyContinue) -join "`n"
        if ($Process.ExitCode -ne 0 -or
            $Output -match '(?im)(error parsing|option .* not found|no such filter|failed to configure|audio filter chain.*failed|could not create)') {
            throw "Audio filter smoke failed for preset $($Preset.Id):`n$Output"
        }
        Write-Host "Audio filter preset '$($Preset.Id)' passed on pinned mpv.net."
    }
}
finally {
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
