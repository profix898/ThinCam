#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

build_macos() {
  local arch="$1"
  local rid="osx-$arch"
  local build="$ROOT/artifacts/$rid"
  cmake -S "$ROOT/native/apple" -B "$build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0
  cmake --build "$build"
  mkdir -p "$ROOT/src/ThinCam/runtimes/$rid/native"
  cp "$build/libthincam.dylib" "$ROOT/src/ThinCam/runtimes/$rid/native/libthincam.dylib"
}

build_ios() {
  local sdk="$1"
  local arch="$2"
  local rid="$3"
  local build="$ROOT/artifacts/$rid"
  cmake -S "$ROOT/native/apple" -B "$build" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_OSX_SYSROOT="$sdk" \
    -DCMAKE_OSX_ARCHITECTURES="$arch" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0
  cmake --build "$build"
  mkdir -p "$ROOT/src/ThinCam/runtimes/$rid/native"
  cp "$build/libthincam.a" "$ROOT/src/ThinCam/runtimes/$rid/native/libthincam.a"
}

build_macos arm64
build_macos x86_64
build_ios iphoneos arm64 ios-arm64
build_ios iphonesimulator arm64 iossimulator-arm64
build_ios iphonesimulator x86_64 iossimulator-x64
