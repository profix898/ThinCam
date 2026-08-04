#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE_ROOT="$ROOT/Build/Native/runtimes"
SKIP_PACK=false
SKIP_ANDROID=false

for arg in "$@"; do
  case "$arg" in
    --skip-pack)    SKIP_PACK=true ;;
    --skip-android) SKIP_ANDROID=true ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

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

resolve_ndk() {
  # Explicit environment variable wins.
  if [[ -n "${ANDROID_NDK_HOME:-}" && -f "$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" ]]; then
    echo "$ANDROID_NDK_HOME"
    return 0
  fi
  local sdk
  for sdk in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "$HOME/Library/Android/sdk" "$HOME/Android/Sdk"; do
    [[ -n "$sdk" && -d "$sdk" ]] || continue
    if [[ -d "$sdk/ndk" ]]; then
      local candidate
      candidate="$(find "$sdk/ndk" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort -V | tail -1)"
      if [[ -n "$candidate" && -f "$candidate/build/cmake/android.toolchain.cmake" ]]; then
        echo "$candidate"
        return 0
      fi
    fi
    if [[ -f "$sdk/ndk-bundle/build/cmake/android.toolchain.cmake" ]]; then
      echo "$sdk/ndk-bundle"
      return 0
    fi
  done
  return 1
}

build_android() {
  if [[ "$SKIP_ANDROID" == "true" ]]; then
    echo "=== Skipping Android (--skip-android) ==="
    return
  fi
  local ndk
  if ! ndk="$(resolve_ndk)"; then
    echo "=== Skipping Android (no NDK found; set ANDROID_NDK_HOME or ANDROID_HOME) ==="
    return
  fi
  local toolchain="$ndk/build/cmake/android.toolchain.cmake"
  local strip_bin
  strip_bin="$(find "$ndk/toolchains/llvm/prebuilt" -name llvm-strip -type f 2>/dev/null | head -1)"
  echo "Using NDK at $ndk"

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
    if [[ -n "$strip_bin" ]]; then
      "$strip_bin" --strip-unneeded "$STAGE_ROOT/$rid/native/libthincam.so" || true
    fi
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
if [[ "$SKIP_PACK" == "false" ]]; then
  pack
fi
echo "=== Done ==="
