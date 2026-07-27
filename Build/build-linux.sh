#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${RID:-linux-x64}"
BUILD_DIR="$ROOT/artifacts/$RID"
STAGE_DIR="$ROOT/Sources/ThinCam/runtimes/$RID/native"

cmake -S "$ROOT/Native/linux" -B "$BUILD_DIR" -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD_DIR"
mkdir -p "$STAGE_DIR"
cp "$BUILD_DIR/libthincam.so" "$STAGE_DIR/libthincam.so"
strip --strip-unneeded "$STAGE_DIR/libthincam.so" 2>/dev/null || true

cmake -S "$ROOT/Native/linux/tests" -B "$ROOT/artifacts/linux-tests" -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build "$ROOT/artifacts/linux-tests"
"$ROOT/artifacts/linux-tests/pixel_convert_tests"

echo "Staged $STAGE_DIR/libthincam.so"
