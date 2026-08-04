#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MpvNetPath,
    [switch]$VerifyAudioWatchdog
)

$ErrorActionPreference = "Stop"
$MpvNetPath = [IO.Path]::GetFullPath($MpvNetPath)
if (-not (Test-Path $MpvNetPath -PathType Leaf)) {
    throw "mpv.net console launcher is missing: $MpvNetPath"
}

$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("censor-runtime-smoke-" + [Guid]::NewGuid().ToString("N"))
$MediaPath = Join-Path $TempRoot "clip.y4m"
$StdOut = Join-Path $TempRoot "stdout.log"
$StdErr = Join-Path $TempRoot "stderr.log"
$PipeName = "censor-runtime-" + [Guid]::NewGuid().ToString("N")
$Process = $null
$Pipe = $null
$Reader = $null
$Writer = $null
$RequestId = 0
$DataRoot = $null
$CreatedDataRoot = $false
New-Item -ItemType Directory $TempRoot | Out-Null

function Invoke-MpvCommand {
    param([Parameter(Mandatory)][object[]]$Command)

    $script:RequestId++
    $Id = $script:RequestId
    $Payload = @{
        command = $Command
        request_id = $Id
    } | ConvertTo-Json -Compress -Depth 20
    $script:Writer.WriteLine($Payload)

    while ($true) {
        $ReadTask = $script:Reader.ReadLineAsync()
        if (-not $ReadTask.Wait(5000)) {
            throw "mpv IPC timed out for command: $($Command -join ' ')"
        }
        $Line = $ReadTask.Result
        if ($null -eq $Line) {
            throw "mpv IPC closed while waiting for: $($Command -join ' ')"
        }
        $Response = $Line | ConvertFrom-Json
        if ($Response.request_id -ne $Id) {
            continue
        }
        if ($Response.error -ne "success") {
            throw "mpv command failed ($($Response.error)): $($Command -join ' ')"
        }
        return $Response
    }
}

function Send-CensorMessage {
    param([Parameter(Mandatory)][string[]]$Arguments)
    [void](Invoke-MpvCommand ([object[]]@("script-message-to", "censor") + $Arguments))
}

function Get-FilterText {
    param([string]$Property = "vf")

    $Response = Invoke-MpvCommand @("get_property", $Property)
    return $Response.data | ConvertTo-Json -Compress -Depth 20
}

function Wait-ForFilter {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][bool]$Present,
        [string]$Property = "vf"
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $Found = (Get-FilterText $Property).Contains($Label, [StringComparison]::Ordinal)
        if ($Found -eq $Present) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Filter '$Label' did not reach expected presence '$Present'. Current ${Property}: $(Get-FilterText $Property)"
}

function Wait-ForBlurIdentity {
    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $Filters = Get-FilterText
        if ($Filters.Contains("censor_blur_000", [StringComparison]::Ordinal) -and
            $Filters.Contains("sigma=40", [StringComparison]::Ordinal) -and
            $Filters.Contains("steps=2", [StringComparison]::Ordinal)) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Blur filter did not return to the expected 40/2 identity. Current vf: $(Get-FilterText)"
}

function Wait-ForAudioIdentity {
    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $Filters = Get-FilterText "af"
        if ($Filters.Contains("censor_audio_compression", [StringComparison]::Ordinal) -and
            $Filters.Contains("acompressor", [StringComparison]::Ordinal) -and
            $Filters.Contains("alimiter", [StringComparison]::Ordinal) -and
            -not $Filters.Contains("volume=0.5", [StringComparison]::Ordinal)) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Audio filter did not return to the Film Balanced identity. Current af: $(Get-FilterText 'af')"
}

function Wait-ForMedia {
    param([Parameter(Mandatory)][string]$ExpectedPath)

    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            $Response = Invoke-MpvCommand @("get_property", "path")
            if ([IO.Path]::GetFullPath([string]$Response.data) -eq $ExpectedPath) {
                return
            }
        }
        catch {
            # The path property is unavailable until loadfile reaches StartFile.
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "mpv.net did not load the runtime-smoke media file."
}

function Wait-ForExtension {
    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        try {
            $Response = Invoke-MpvCommand @("get_property", "user-data/censor/ready")
            if ($Response.data -eq "yes") {
                return
            }
        }
        catch {
            # The property appears after the extension subscribes to media events.
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Censor extension did not publish its ready marker."
}

try {
    if ($VerifyAudioWatchdog) {
        if ($env:GITHUB_ACTIONS -ne "true") {
            throw "Audio watchdog smoke is CI-only because it creates temporary CensorPlayer settings."
        }
        $LocalData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($LocalData)) {
            throw "Windows LocalApplicationData is unavailable."
        }
        $DataRoot = Join-Path $LocalData "CensorPlayer"
        if (Test-Path $DataRoot) {
            throw "Audio watchdog smoke refuses to overwrite an existing CensorPlayer data directory."
        }
        New-Item -ItemType Directory $DataRoot | Out-Null
        $CreatedDataRoot = $true
        [IO.File]::WriteAllText(
            (Join-Path $DataRoot "settings.json"),
            '{"schema":1,"watchdogEnabled":true,"watchdogIntervalMs":250,"audioCompressionPreset":"film-balanced"}',
            [Text.UTF8Encoding]::new($false))
    }

    $Header = [Text.Encoding]::ASCII.GetBytes("YUV4MPEG2 W64 H64 F10:1 Ip A1:1 C420jpeg`n")
    $FrameHeader = [Text.Encoding]::ASCII.GetBytes("FRAME`n")
    $Frame = [byte[]]::new(64 * 64 * 3 / 2)
    [Array]::Fill($Frame, [byte]128)
    $Video = [IO.File]::Create($MediaPath)
    try {
        $Video.Write($Header, 0, $Header.Length)
        for ($Index = 0; $Index -lt 60; $Index++) {
            $Video.Write($FrameHeader, 0, $FrameHeader.Length)
            $Video.Write($Frame, 0, $Frame.Length)
        }
    }
    finally {
        $Video.Dispose()
    }
    $Process = Start-Process $MpvNetPath `
        -ArgumentList @(
            "--idle=yes",
            "--keep-open=yes",
            "--input-terminal=no",
            "--vo=null",
            "--ao=null",
            "--msg-level=all=warn",
            "--image-display-duration=60",
            "--loop-file=inf",
            "--input-ipc-server=\\.\pipe\$PipeName"
        ) `
        -RedirectStandardOutput $StdOut `
        -RedirectStandardError $StdErr `
        -PassThru

    $Pipe = [IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    $Pipe.Connect(15000)
    $Reader = [IO.StreamReader]::new($Pipe, [Text.UTF8Encoding]::new($false), $false, 4096, $true)
    $Writer = [IO.StreamWriter]::new($Pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
    $Writer.AutoFlush = $true

    Wait-ForExtension
    [void](Invoke-MpvCommand @("loadfile", $MediaPath, "replace"))
    Wait-ForMedia $MediaPath
    [void](Invoke-MpvCommand @("vf", "add", "@censor_smoke_user:lavfi=[hflip]"))
    Wait-ForFilter "censor_smoke_user" $true
    Wait-ForFilter "censor_blur_000" $false
    Send-CensorMessage @("censor-mark-start")
    Start-Sleep -Milliseconds 2000
    Send-CensorMessage @("censor-mark-end")
    Start-Sleep -Milliseconds 1000
    Send-CensorMessage @("censor-apply")
    Wait-ForFilter "censor_blur_000" $true
    Wait-ForFilter "censor_smoke_user" $true

    $Pause = Invoke-MpvCommand @("get_property", "pause")
    if ($Pause.data -ne $false) {
        throw "Extension leaked pause after applying the blur graph."
    }

    # IPC mutations are synchronous: the wrong same-label graph exists before
    # the watchdog can claim a successful identity check.
    [void](Invoke-MpvCommand @("vf", "remove", "@censor_blur_000"))
    [void](Invoke-MpvCommand @(
        "vf",
        "add",
        "@censor_blur_000:lavfi=[gblur=sigma=1:steps=1]"))
    [void](Invoke-MpvCommand @("vf", "add", "@censor_blur_stale:lavfi=[vflip]"))
    Wait-ForBlurIdentity
    Wait-ForFilter "censor_blur_stale" $false

    if ($VerifyAudioWatchdog) {
        Wait-ForAudioIdentity
        [void](Invoke-MpvCommand @("af", "remove", "@censor_audio_compression"))
        [void](Invoke-MpvCommand @(
            "af",
            "add",
            "@censor_audio_compression:lavfi=[volume=0.5]"))
        Wait-ForAudioIdentity
    }

    Send-CensorMessage @("censor-disable")
    Wait-ForFilter "censor_blur_000" $false
    Wait-ForFilter "censor_smoke_user" $true

    [void](Invoke-MpvCommand @("quit"))
    if (-not $Process.WaitForExit(15000)) {
        throw "mpv.net did not exit after the runtime smoke."
    }
    $Output = (Get-Content $StdOut, $StdErr -Raw -ErrorAction SilentlyContinue) -join "`n"
    if ($Process.ExitCode -ne 0 -or
        $Output -match '(?im)(ReflectionTypeLoadException|Could not load file or assembly|error running command)') {
        throw "Extension runtime smoke reported an error:`n$Output"
    }
    Write-Host "Extension apply, exact-identity watchdog recovery, pause ownership, and disable smoke passed."
}
catch {
    $Output = (Get-Content $StdOut, $StdErr -Raw -ErrorAction SilentlyContinue) -join "`n"
    throw "$($_.Exception.Message)`n$Output"
}
finally {
    if ($null -ne $Writer) { $Writer.Dispose() }
    if ($null -ne $Reader) { $Reader.Dispose() }
    if ($null -ne $Pipe) { $Pipe.Dispose() }
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit()
    }
    if ($CreatedDataRoot) {
        Remove-Item $DataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
