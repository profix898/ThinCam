#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE_ROOT="$ROOT/Build/Native/runtimes"
RID="${RID:-linux-x64}"
BUILD_DIR="$ROOT/artifacts/$RID"
STAGE_DIR="$STAGE_ROOT/$RID/native"
SKIP_PACK=false

[[ "${1:-}" == "--skip-pack" ]] && SKIP_PACK=true

build_linux() {
  echo "=== Building $RID ==="
  cmake -S "$ROOT/Native/linux" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release
  cmake --build "$BUILD_DIR"
  mkdir -p "$STAGE_DIR"
  cp "$BUILD_DIR/libthincam.so" "$STAGE_DIR/libthincam.so"
  strip --strip-unneeded "$STAGE_DIR/libthincam.so" 2>/dev/null || true

  echo "=== Running pixel conversion tests ==="
  cmake -S "$ROOT/Native/linux/tests" -B "$ROOT/artifacts/linux-tests" -G Ninja -DCMAKE_BUILD_TYPE=Release
  cmake --build "$ROOT/artifacts/linux-tests"
  "$ROOT/artifacts/linux-tests/pixel_convert_tests"

  echo "Staged $STAGE_DIR/libthincam.so"
}

build_android() {
  if [[ -z "${ANDROID_NDK_HOME:-}" ]]; then
    echo "=== Skipping Android (ANDROID_NDK_HOME not set) ==="
    return
  fi
  local toolchain="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
  local abis=("arm64-v8a android-arm64" "x86_64 android-x64")
  for pair in "${abis[@]}"; do
    local abi="${pair%% *}"
    local rid="${pair##* }"
    local build="$ROOT/artifacts/$rid"
    echo "=== Building Android $abi ==="
    cmake -S "$ROOT/Native/android" -B "$build" -G Ninja \
      -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE="$toolchain" \
      -DANDROID_ABI="$abi" \
      -DANDROID_PLATFORM=android-24 \
      -DANDROID_STL=c++_static
    cmake --build "$build"
    mkdir -p "$STAGE_ROOT/$rid/native"
    cp "$build/libthincam.so" "$STAGE_ROOT/$rid/native/libthincam.so"
    echo "Staged $STAGE_ROOT/$rid/native/libthincam.so"
  done
}

pack() {
  echo "=== Packaging NuGet packages ==="
  local pack_args=(-c Release -o "$ROOT/artifacts/packages")
  if [[ -n "${VERSION:-}" ]]; then
    pack_args+=("-p:PackageVersion=$VERSION")
  fi
  if [[ -n "${TARGET_FRAMEWORKS:-}" ]]; then
    pack_args+=("-p:ThinCamTargetFrameworks=$TARGET_FRAMEWORKS")
  fi
  dotnet pack "$ROOT/Sources/ThinCam/ThinCam.csproj" "${pack_args[@]}"
  dotnet pack "$ROOT/Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj" "${pack_args[@]}"
  dotnet pack "$ROOT/Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj" "${pack_args[@]}"
}

build_linux
build_android
if [[ "$SKIP_PACK" == "false" ]]; then
  pack
fi
echo "=== Done ==="
