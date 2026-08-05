#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This script must run on macOS." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

PUBLISH="$ROOT/Build/Native/artifacts/demo-macos/publish"
APP="$ROOT/Build/Native/artifacts/demo-macos/ThinCam Demo.app"
CONTENTS="$APP/Contents"

rm -rf "$ROOT/Build/Native/artifacts/demo-macos"
mkdir -p "$PUBLISH" "$CONTENTS/MacOS" "$CONTENTS/Resources"

dotnet publish "$ROOT/Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj"   -c Release   -r "$RID"   --self-contained false   -o "$PUBLISH"

cp -R "$PUBLISH"/. "$CONTENTS/MacOS/"
cp "$ROOT/Samples/ThinCamDemo.Desktop/Info.plist" "$CONTENTS/Info.plist"
chmod +x "$CONTENTS/MacOS/ThinCamDemo.Desktop"

codesign --force --deep --sign - "$APP"
open "$APP"
