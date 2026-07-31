#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="7.1.2.0"
source_commit="96afc62165b5a1df186fc79c58813f37061ace9a"
source_dir="$repo_root/.deps/mpvnet-source"
reference_dir="$repo_root/.deps/mpvnet"

mkdir -p "$reference_dir"

if [[ ! -d "$source_dir/.git" ]]; then
  git clone --depth 1 --branch "v${version}" https://github.com/mpvnet-player/mpv.net.git "$source_dir"
fi

actual_commit="$(git -C "$source_dir" rev-parse HEAD)"
if [[ "$actual_commit" != "$source_commit" ]]; then
  echo "mpv.net source commit mismatch" >&2
  exit 1
fi

dotnet_cmd="${DOTNET_CMD:-dotnet}"
"$dotnet_cmd" build "$source_dir/src/MpvNet/MpvNet.csproj" --configuration Release

source_dll="$source_dir/src/MpvNet/bin/Release/libmpvnet.dll"
if [[ ! -f "$source_dll" ]]; then
  echo "libmpvnet.dll was not produced" >&2
  exit 1
fi

cp "$source_dll" "$reference_dir/libmpvnet.dll"

printf 'restored mpv.net %s reference from %s\n' "$version" "$source_commit"
