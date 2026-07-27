#!/usr/bin/env bash
set -euo pipefail

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an Android NDK installation.}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"

build_abi() {
  local abi="$1"
  local rid="$2"
  local build="$ROOT/artifacts/$rid"
  cmake -S "$ROOT/Native/android" -B "$build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
    -DANDROID_ABI="$abi" \
    -DANDROID_PLATFORM=android-24 \
    -DANDROID_STL=c++_static
  cmake --build "$build"
  mkdir -p "$ROOT/Sources/ThinCam/runtimes/$rid/native"
  cp "$Build/libthincam.so" "$ROOT/Sources/ThinCam/runtimes/$rid/native/libthincam.so"
}

build_abi arm64-v8a android-arm64
build_abi x86_64 android-x64
