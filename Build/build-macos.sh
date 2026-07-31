#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE_ROOT="$ROOT/Build/Native/runtimes"

build_macos() {
  local arch="$1"
  local rid="osx-$arch"
  local build="$ROOT/artifacts/$rid"
  echo "=== Building macOS $arch ==="
  cmake -S "$ROOT/Native/apple" -B "$build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0
  cmake --build "$build"
  mkdir -p "$STAGE_ROOT/$rid/native"
  cp "$build/libthincam.dylib" "$STAGE_ROOT/$rid/native/libthincam.dylib"
  echo "Staged $STAGE_ROOT/$rid/native/libthincam.dylib"
}

build_ios() {
  local sdk="$1"
  local arch="$2"
  local rid="$3"
  local build="$ROOT/artifacts/$rid"
  echo "=== Building iOS $rid ==="
  cmake -S "$ROOT/Native/apple" -B "$build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_OSX_SYSROOT="$sdk" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0
  cmake --build "$build"
  mkdir -p "$STAGE_ROOT/$rid/native"
  cp "$build/libthincam.a" "$STAGE_ROOT/$rid/native/libthincam.a"
  echo "Staged $STAGE_ROOT/$rid/native/libthincam.a"
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

build_macos arm64
build_macos x86_64
build_ios iphoneos arm64 ios-arm64
build_ios iphonesimulator arm64 iossimulator-arm64
build_ios iphonesimulator x86_64 iossimulator-x64
build_android
pack
echo "=== Done ==="
