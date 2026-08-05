#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STAGE_ROOT="$ROOT/Build/Native/runtimes"
ARTIFACTS="$ROOT/Build/Native/artifacts"
RID="${RID:-linux-x64}"
BUILD_DIR="$ARTIFACTS/$RID"
STAGE_DIR="$STAGE_ROOT/$RID/native"
SKIP_PACK=false
SKIP_ANDROID=false

for arg in "$@"; do
  case "$arg" in
    --skip-pack)    SKIP_PACK=true ;;
    --skip-android) SKIP_ANDROID=true ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

build_linux() {
  echo "=== Building $RID ==="
  cmake -S "$ROOT/Native/linux" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release
  cmake --build "$BUILD_DIR"
  mkdir -p "$STAGE_DIR"
  cp "$BUILD_DIR/libthincam.so" "$STAGE_DIR/libthincam.so"
  strip --strip-unneeded "$STAGE_DIR/libthincam.so" 2>/dev/null || true

  echo "=== Running pixel conversion tests ==="
  cmake -S "$ROOT/Native/linux/tests" -B "$ARTIFACTS/linux-tests" -G Ninja -DCMAKE_BUILD_TYPE=Release
  cmake --build "$ARTIFACTS/linux-tests"
  "$ARTIFACTS/linux-tests/pixel_convert_tests"

  echo "Staged $STAGE_DIR/libthincam.so"
}

resolve_ndk() {
  # Explicit environment variable wins.
  if [[ -n "${ANDROID_NDK_HOME:-}" && -f "$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" ]]; then
    echo "$ANDROID_NDK_HOME"
    return 0
  fi
  local sdk
  for sdk in "${ANDROID_HOME:-}" "${ANDROID_SDK_ROOT:-}" "$HOME/Android/Sdk" "$HOME/Library/Android/sdk"; do
    [[ -n "$sdk" && -d "$sdk" ]] || continue
    # Side-by-side versioned NDKs, newest first.
    if [[ -d "$sdk/ndk" ]]; then
      local candidate
      candidate="$(find "$sdk/ndk" -maxdepth 1 -mindepth 1 -type d 2>/dev/null | sort -V | tail -1)"
      if [[ -n "$candidate" && -f "$candidate/build/cmake/android.toolchain.cmake" ]]; then
        echo "$candidate"
        return 0
      fi
    fi
    # Legacy single-NDK layout.
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
  strip_bin="$(find "$ndk/toolchains/llvm/prebuilt" -name llvm-strip \( -type f -o -type l \) 2>/dev/null | head -1)"
  echo "Using NDK at $ndk"

  local abis=("arm64-v8a android-arm64" "x86_64 android-x64")
  for pair in "${abis[@]}"; do
    local abi="${pair%% *}"
    local rid="${pair##* }"
    local build="$ARTIFACTS/$rid"
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
  local pack_args=(-c Release -o "$ROOT/Build/Native/artifacts/packages")
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
