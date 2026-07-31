# Windows Phase 0 checklist

This checklist records runtime evidence that cross-compilation and GitHub
Actions cannot provide.

## Setup

- Use mpv.net `v7.1.2.0` from `deps.lock.json`.
- Copy the single `CensorExtension.dll` into
  `<MPVNET_CONFIG>\extensions\CensorExtension\`.
- Remove legacy `Censor.Core.dll` and `CensorExtension.deps.json` from the
  previous failed artifact before the cold start.
- Merge `examples/input.conf` into `<MPVNET_CONFIG>\input.conf`.
- Record GPU, driver, Windows build, mpv.net commit, libmpv, and FFmpeg versions.

## Blocking checks

- [ ] `Ctrl+Alt+c` opens one responsive tool window.
- [ ] Picker and drag-and-drop load TXT, SRT, and WebVTT.
- [ ] A labeled `@censor_blur_000` filter with the default
  `gblur=sigma=40:steps=2`
  appears in `vf` readback.
- [ ] The blur selector re-applies the active schedule as `Strong (30/2)`,
  `Balanced (40/2)`, and `Maximum (50/3)` without removing user filters.
- [ ] A pre-existing user video filter survives apply, disable, and recovery.
- [ ] Blur starts at `start_ms` and is absent at exact `end_ms`.
- [ ] FFmpeg `t` matches mpv `time-pos` for normal media and non-zero start time.
- [ ] Seek backward/forward, pause/resume, chapters, watch-later, and speeds
  `0.5x`, `1x`, `1.5x`, and `2x` do not drift.
- [ ] A → B during parse/apply never applies A's schedule to B.
- [ ] Pressing `censor-disable` exactly as apply completes leaves the window
  `DISABLED` and never emits a later `ACTIVE` OSD.
- [ ] While the duration-mismatch dialog is open, a second load/disable and
  mpv.net exit cannot publish stale status, hang, or leave a visible dialog.
- [ ] Removing a censor filter triggers bounded recovery without pause leakage.
- [ ] Exit during apply/recovery does not hang or crash mpv.net.

## Performance

- [ ] 1,000 intervals parse, normalize, and compile within 250 ms.
- [ ] Warm picker-to-summary flow completes within 500 ms.
- [ ] Default `Balanced` blur is usable at 1080p30, 1080p60, and 4K30;
  dropped frames are recorded for all three presets.
- [ ] Software decoding fallback is documented.
- [ ] OBS Window Capture receives the already blurred frame.

Any failed blocking check keeps the release experimental and must be recorded
with logs, media properties, exact schedule, and `vf` readback.
