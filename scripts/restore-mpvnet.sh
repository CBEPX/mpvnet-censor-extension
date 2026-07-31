#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IFS=$'\t' read -r version tag source_commit compile_reference_sha256 < <(
  node -e '
    const lock = require(process.argv[1]);
    console.log([
      lock.mpvNet.version,
      lock.mpvNet.tag,
      lock.mpvNet.sourceCommit,
      lock.mpvNet.compileReferenceSha256,
    ].join("\t"));
  ' "$repo_root/deps.lock.json"
)
source_dir="$repo_root/.deps/mpvnet-source"
reference_dir="$repo_root/.deps/mpvnet"

mkdir -p "$reference_dir"

if [[ ! -d "$source_dir/.git" ]]; then
  git clone --depth 1 --branch "$tag" https://github.com/mpvnet-player/mpv.net.git "$source_dir"
fi

actual_commit="$(git -C "$source_dir" rev-parse HEAD)"
if [[ "$actual_commit" != "$source_commit" ]]; then
  echo "mpv.net source commit mismatch" >&2
  exit 1
fi
if ! git -C "$source_dir" diff --quiet ||
  ! git -C "$source_dir" diff --cached --quiet; then
  echo "mpv.net source checkout contains tracked modifications" >&2
  exit 1
fi

dotnet_cmd="${DOTNET_CMD:-dotnet}"
"$dotnet_cmd" build "$source_dir/src/MpvNet/MpvNet.csproj" --configuration Release

source_dll="$source_dir/src/MpvNet/bin/Release/libmpvnet.dll"
if [[ ! -f "$source_dll" ]]; then
  echo "libmpvnet.dll was not produced" >&2
  exit 1
fi
actual_reference_sha256="$(
  node -e '
    const crypto = require("node:crypto");
    const fs = require("node:fs");
    console.log(crypto.createHash("sha256").update(fs.readFileSync(process.argv[1])).digest("hex"));
  ' "$source_dll"
)"
if [[ "$actual_reference_sha256" != "$compile_reference_sha256" ]]; then
  echo "libmpvnet.dll SHA-256 mismatch" >&2
  exit 1
fi

cp "$source_dll" "$reference_dir/libmpvnet.dll"

printf 'restored mpv.net %s reference from %s\n' "$version" "$source_commit"
