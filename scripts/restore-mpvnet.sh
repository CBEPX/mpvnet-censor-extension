#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_cmd="${DOTNET_CMD:-dotnet}"
if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  echo ".NET SDK is required to read deps.lock.json" >&2
  exit 1
fi

case "$(uname -s):$(uname -m)" in
  Darwin:arm64) platform="osx-arm64" ;;
  MINGW*:x86_64|MSYS*:x86_64|CYGWIN*:x86_64) platform="windows-x64" ;;
  *)
    echo "Unsupported compile-reference platform: $(uname -s) $(uname -m)" >&2
    exit 1
    ;;
esac

if ! lock_values="$(
  "$dotnet_cmd" run --file "$repo_root/scripts/deps-lock.cs" -- \
    read "$repo_root/deps.lock.json" "$platform"
)"; then
  echo "Unable to read mpv.net values from deps.lock.json" >&2
  exit 1
fi
IFS=$'\t' read -r version tag source_commit expected_reference_sha256 <<<"$lock_values"
if [[ -z "$version" || -z "$tag" || -z "$source_commit" ]]; then
  echo "deps.lock.json has incomplete mpv.net values" >&2
  exit 1
fi
if [[ -z "$expected_reference_sha256" ]]; then
  echo "deps.lock.json has no compile-reference SHA-256 for $platform" >&2
  exit 1
fi
source_dir="$repo_root/.deps/mpvnet-source"
reference_dir="$repo_root/.deps/mpvnet"

mkdir -p "$reference_dir"

if [[ ! -d "$source_dir/.git" ]]; then
  git clone --depth 1 --branch "$tag" https://github.com/mpvnet-player/mpv.net.git "$source_dir"
fi

if [[ -n "$(git -C "$source_dir" status --porcelain --untracked-files=all -- \
  . ':(exclude).serena/**')" ]]; then
  echo "mpv.net source checkout contains tracked or untracked modifications" >&2
  exit 1
fi
actual_commit="$(git -C "$source_dir" rev-parse HEAD)"
if [[ "$actual_commit" != "$source_commit" ]]; then
  git -C "$source_dir" fetch --depth 1 origin "$tag"
  git -C "$source_dir" checkout --detach FETCH_HEAD
  actual_commit="$(git -C "$source_dir" rev-parse HEAD)"
fi
if [[ "$actual_commit" != "$source_commit" ]]; then
  echo "mpv.net source commit mismatch" >&2
  exit 1
fi

"$dotnet_cmd" build "$source_dir/src/MpvNet/MpvNet.csproj" \
  --configuration Release \
  -p:ContinuousIntegrationBuild=true \
  -p:DebugType=none \
  -p:PathMap="$source_dir=/_/"

source_dll="$source_dir/src/MpvNet/bin/Release/libmpvnet.dll"
if [[ ! -f "$source_dll" ]]; then
  echo "libmpvnet.dll was not produced" >&2
  exit 1
fi
actual_reference_sha256="$(
  "$dotnet_cmd" run --file "$repo_root/scripts/deps-lock.cs" -- sha256 "$source_dll"
)"
printf 'compile reference sha256 (%s): %s\n' "$platform" "$actual_reference_sha256"
if [[ "$actual_reference_sha256" != "$expected_reference_sha256" ]]; then
  echo "libmpvnet.dll SHA-256 mismatch for $platform" >&2
  exit 1
fi
cp "$source_dll" "$reference_dir/libmpvnet.dll"

printf 'restored mpv.net %s reference from %s\n' "$version" "$source_commit"
