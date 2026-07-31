# mpv.net Censor Extension

Experimental Windows extension for stock [mpv.net](https://github.com/mpvnet-player/mpv.net).
It reads exact-name `.censor.txt`, `.censor.srt`, or `.censor.vtt` sidecars and
compiles their intervals into labeled FFmpeg `gblur` filters scoped to the
current media session.

## Build

Requires .NET SDK 10.0.302.

```sh
bash scripts/restore-mpvnet.sh
dotnet restore CensorPlayer.sln --locked-mode
dotnet build CensorPlayer.sln --configuration Release --no-restore
dotnet test CensorPlayer.sln --configuration Release --no-build --no-restore
```

The extension output is the single assembly
`src/Censor.MpvNet.Extension/bin/Release/CensorExtension.dll`. Core sources are
compiled into that assembly because stock mpv.net loads extensions with
`Assembly.LoadFile` and does not resolve sibling project dependencies.

## Install and open

Copy `CensorExtension.dll` into:

```text
<MPVNET_CONFIG>\extensions\CensorExtension\
```

When replacing the earlier three-file artifact, remove the legacy
`Censor.Core.dll` and `CensorExtension.deps.json` first.

mpv.net requires the directory and primary DLL to have the same
`CensorExtension` name. Merge [examples/input.conf](examples/input.conf) into
your mpv.net `input.conf`, then press `Ctrl+Alt+c`.

The tool window supports file picking, drag-and-drop, reload, and disabling the
current session. You can also use:

```text
script-message-to censor censor-open
script-message-to censor censor-load "C:\path\Film.censor.txt"
script-message-to censor censor-reload
script-message-to censor censor-disable
```

## Current proof boundary

- Core parser, subtitle adapters, normalization, atomic save, and filter
  compilation are covered by deterministic and property-based tests.
- The extension cross-builds against pinned mpv.net `v7.1.2.0`.
- Windows CI repeats the stock `Assembly.LoadFile` + `GetTypes` loader contract
  and rejects any external `Censor.Core` assembly reference.
- Windows CI publishes a `CensorExtension-windows` artifact.
- Real playback timing, `gblur`, seek/speed behavior, GPU performance, and
  mpv.net UI integration still require the documented Windows Phase 0 run.

See [the specification](docs/TZ.md), [ADR-003](docs/adr/ADR-003-session-scoped-schedules-no-database.md),
[the execution plan](task_plan.md), and [the Windows checklist](docs/WINDOWS_PHASE0.md).
