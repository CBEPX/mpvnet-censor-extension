# Third-party notices

CensorPlayer is licensed under `GPL-2.0-only`. Package-validation jobs use the
pinned stock mpv.net portable runtime. Binary hashes and known upstream source
coordinates are recorded in `deps.lock.json`.

The full portable runtime and installer are not published yet. Their
corresponding-source set is incomplete because the exact source revisions for
all statically linked libmpv dependencies have not been established. CI may
build and test those packages on an ephemeral runner, but only the extension
DLL and this project's source may be uploaded.

## mpv.net

- Project: <https://github.com/mpvnet-player/mpv.net>
- Pinned version/commit: see `deps.lock.json`
- License: GNU General Public License version 2
- License text in packages: `LICENSES/mpv.net-GPL-2.0.txt`

## mpv / libmpv

- Project: <https://github.com/mpv-player/mpv>
- Pinned commit: see `deps.lock.json`
- License: GPL-2.0-or-later by default; the effective binary license depends
  on its configured optional components.
- The known mpv and `mpv-winbuild-cmake` revisions are provenance hints, not a
  claim of complete corresponding source.

## FFmpeg

- Project: <https://github.com/FFmpeg/FFmpeg>
- Pinned commit: see `deps.lock.json`
- License: LGPL-2.1-or-later with GPL-licensed optional components; the
  bundled libmpv build is redistributed under the enclosing GPL terms.
- The known FFmpeg revision is a provenance hint, not a claim of complete
  corresponding source.

## Other files in the pinned mpv.net runtime

The portable runtime also contains MediaInfo and Microsoft/.NET desktop
runtime components. Package validation records their filenames and SHA-256
values in an SPDX SBOM. Those components retain their respective upstream
copyrights and redistribution terms; no ownership is claimed by CensorPlayer.

This notice is informational and is not legal advice. Publication remains
blocked until the complete corresponding-source set and all redistribution
notices have been verified.
