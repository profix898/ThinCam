#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACK_ARGS=(-c Release -o "$ROOT/artifacts/packages")
if [[ -n "${VERSION:-}" ]]; then
  PACK_ARGS+=("-p:PackageVersion=$VERSION")
fi
if [[ -n "${TARGET_FRAMEWORKS:-}" ]]; then
  PACK_ARGS+=("-p:ThinCamTargetFrameworks=$TARGET_FRAMEWORKS")
fi

dotnet pack "$ROOT/Sources/ThinCam/ThinCam.csproj" "${PACK_ARGS[@]}"
dotnet pack "$ROOT/Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj" "${PACK_ARGS[@]}"
dotnet pack "$ROOT/Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj" "${PACK_ARGS[@]}"
