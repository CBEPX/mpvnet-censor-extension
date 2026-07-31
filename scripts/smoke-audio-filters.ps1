#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MpvNetPath,
    [string]$CoreAssemblyPath = "src/Censor.Core/bin/Release/net10.0/Censor.Core.dll"
)

$ErrorActionPreference = "Stop"
$MpvNetPath = [IO.Path]::GetFullPath($MpvNetPath)
$CoreAssemblyPath = [IO.Path]::GetFullPath($CoreAssemblyPath)
if (-not (Test-Path $MpvNetPath -PathType Leaf)) {
    throw "mpv.net console launcher is missing: $MpvNetPath"
}
if (-not (Test-Path $CoreAssemblyPath -PathType Leaf)) {
    throw "Censor.Core assembly is missing: $CoreAssemblyPath"
}

$Assembly = [Reflection.Assembly]::LoadFrom($CoreAssemblyPath)
$CatalogType = $Assembly.GetType("Censor.Core.AudioCompressionPresets", $true)
$Presets = $CatalogType.GetProperty("All").GetValue($null) |
    Where-Object { $null -ne $_.Filter }
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("censor-audio-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $TempRoot | Out-Null

try {
    foreach ($Preset in $Presets) {
        $StdOut = Join-Path $TempRoot "$($Preset.Id).stdout.log"
        $StdErr = Join-Path $TempRoot "$($Preset.Id).stderr.log"
        $Process = Start-Process $MpvNetPath `
            -ArgumentList @(
                "--no-config",
                "--process-instance=multi",
                "--load-scripts=no",
                "--input-terminal=no",
                "--idle=no",
                "--keep-open=no",
                "--video=no",
                "--ao=null",
                "--length=0.2",
                "--msg-level=all=warn",
                "--af-add=$($Preset.Filter)",
                "av://lavfi:anullsrc=r=48000:cl=stereo:d=0.2"
            ) `
            -RedirectStandardOutput $StdOut `
            -RedirectStandardError $StdErr `
            -PassThru
        if (-not $Process.WaitForExit(15000)) {
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
