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

The extension output is
`src/Censor.MpvNet.Extension/bin/Release/CensorExtension.dll` together with
`Censor.Core.dll`.

## Current proof boundary

- Core parser, subtitle adapters, normalization, atomic save, and filter
  compilation are covered by deterministic and property-based tests.
- The extension cross-builds against pinned mpv.net `v7.1.2.0`.
- Real playback timing, `gblur`, seek/speed behavior, GPU performance, and
  mpv.net UI integration still require the documented Windows Phase 0 run.

See [the specification](docs/TZ.md), [ADR-003](docs/adr/ADR-003-session-scoped-schedules-no-database.md),
and [the execution plan](task_plan.md).
