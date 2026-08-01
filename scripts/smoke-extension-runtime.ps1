#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MpvNetPath
)

$ErrorActionPreference = "Stop"
$MpvNetPath = [IO.Path]::GetFullPath($MpvNetPath)
if (-not (Test-Path $MpvNetPath -PathType Leaf)) {
    throw "mpv.net console launcher is missing: $MpvNetPath"
}

$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("censor-runtime-smoke-" + [Guid]::NewGuid().ToString("N"))
$MediaPath = Join-Path $TempRoot "frame.ppm"
$SchedulePath = Join-Path $TempRoot "frame.censor.txt"
$StdOut = Join-Path $TempRoot "stdout.log"
$StdErr = Join-Path $TempRoot "stderr.log"
$PipeName = "censor-runtime-" + [Guid]::NewGuid().ToString("N")
$Process = $null
$Pipe = $null
$Reader = $null
$Writer = $null
$RequestId = 0
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
    $Response = Invoke-MpvCommand @("get_property", "vf")
    return $Response.data | ConvertTo-Json -Compress -Depth 20
}

function Wait-ForFilter {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][bool]$Present
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $Found = (Get-FilterText).Contains($Label, [StringComparison]::Ordinal)
        if ($Found -eq $Present) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw "Filter '$Label' did not reach expected presence '$Present'. Current vf: $(Get-FilterText)"
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

try {
    $Header = [Text.Encoding]::ASCII.GetBytes("P6`n64 64`n255`n")
    $Pixels = [byte[]]::new(64 * 64 * 3)
    for ($Index = 0; $Index -lt $Pixels.Length; $Index++) {
        $Pixels[$Index] = 128
    }
    $Image = [byte[]]::new($Header.Length + $Pixels.Length)
    [Buffer]::BlockCopy($Header, 0, $Image, 0, $Header.Length)
    [Buffer]::BlockCopy($Pixels, 0, $Image, $Header.Length, $Pixels.Length)
    [IO.File]::WriteAllBytes($MediaPath, $Image)
    [IO.File]::WriteAllText(
        $SchedulePath,
        "# censor-timeline: 1`n00:00:00.000 --> 00:00:30.000`n",
        [Text.UTF8Encoding]::new($false))

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

    Start-Sleep -Seconds 1
    [void](Invoke-MpvCommand @("loadfile", $MediaPath, "replace"))
    Wait-ForMedia $MediaPath
    [void](Invoke-MpvCommand @("vf", "add", "@censor_smoke_user:lavfi=[hflip]"))
    Wait-ForFilter "censor_smoke_user" $true
    Wait-ForFilter "censor_blur_000" $true
    Wait-ForFilter "censor_smoke_user" $true

    $Pause = Invoke-MpvCommand @("get_property", "pause")
    if ($Pause.data -ne $false) {
        throw "Extension leaked pause after applying the blur graph."
    }

    [void](Invoke-MpvCommand @("vf", "remove", "@censor_blur_000"))
    Wait-ForFilter "censor_blur_000" $true

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
    Write-Host "Extension apply, watchdog recovery, pause ownership, and disable smoke passed."
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
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
