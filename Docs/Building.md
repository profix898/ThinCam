# Building ThinCam

This guide covers native backend compilation, managed `.csproj` builds, sample execution, runtime asset staging, NuGet packaging, and CI-equivalent commands.

ThinCam is not built as one universal binary. Each native backend must be built on its host platform or with a correctly configured cross toolchain. The resulting native files are staged under `Build/Native/runtimes/<rid>/native`, after which the managed project can be built and packed.

## Contents

- [Build flow](#1-build-flow-at-a-glance)
- [Repository requirements](#2-repository-requirements)
- [Runtime asset locations](#3-runtime-asset-locations)
- [Linux native backend](#4-linux-native-backend)
- [Windows native backend](#5-windows-native-backend)
- [macOS and iOS native backend](#6-macos-and-ios-native-backend)
- [Android native backend](#7-android-native-backend)
- [Managed project builds](#8-managed-project-builds)
- [Publishing a desktop consumer](#9-publishing-a-desktop-consumer)
- [NuGet packaging](#10-nuget-packaging)
- [CI-equivalent commands](#11-ci-equivalent-commands)
- [Clean builds](#12-clean-builds)
- [Build troubleshooting](#13-build-troubleshooting)
- [Release build checklist](#14-release-build-checklist)

## 1. Build flow at a glance

```text
1. Install platform native toolchain
2. Build native backend
3. Verify native file is staged under Build/Native/runtimes
4. Install .NET 10 SDK
5. Install Android/iOS workloads when building mobile targets
6. Build the desired ThinCam target framework
7. build/run a sample or host application
8. Pack only after all intended native assets are staged
```

## 2. Repository requirements

### .NET SDK

The project targets .NET 10. Install the SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>. The repository does not pin a specific SDK version via `global.json`; any .NET 10 SDK (10.0.300 or newer) is sufficient. The `Directory.Build.props` file sets the target frameworks (`net10.0;net10.0-android;net10.0-ios`) and the `Directory.Packages.props` file manages NuGet package versions centrally. A repo-local `NuGet.config` pins a single package source (nuget.org) so restore is hermetic regardless of machine-level sources. Install a .NET 10 SDK compatible with this policy. The SDK builds the .NET 10 desktop and mobile targets.

Check the selected SDK:

```bash
dotnet --version
```

### CMake and C++

All native projects use CMake. Minimum project requirements are:

- CMake 3.20 for Windows, Linux, and Apple.
- CMake 3.22 for Android.
- C++17 compiler.

The Unix scripts use Ninja. The Windows script uses the Visual Studio CMake generator.

### Source checkout

```bash
git clone <repository-url> ThinCam
cd ThinCam
```

All commands below assume the repository root as the current directory.

## 3. Runtime asset locations

Build scripts stage artifacts into the managed project:

```text
Build/Native/runtimes/<rid>/native/<library>
```

Expected files are:

| Runtime identifier   | File               |
| -------------------- | ------------------ |
| `win-x64`            | `thincam.dll`      |
| `win-arm64`          | `thincam.dll`      |
| `linux-x64`          | `libthincam.so`    |
| `osx-x64`            | `libthincam.dylib` |
| `osx-arm64`          | `libthincam.dylib` |
| `android-arm64`      | `libthincam.so`    |
| `android-x64`        | `libthincam.so`    |
| `ios-arm64`          | `libthincam.a`     |
| `iossimulator-arm64` | `libthincam.a`     |
| `iossimulator-x64`   | `libthincam.a`     |

Inspect staged files with:

```bash
find Build/Native/runtimes -maxdepth 4 -type f -print
```

On PowerShell:

```powershell
Get-ChildItem Build/Native/runtimes -Recurse -File
```

## 4. Linux native backend

### 4.1 Prerequisites

On Ubuntu/Debian:

```bash
sudo apt-get update
sudo apt-get install -y \
  build-essential \
  cmake \
  ninja-build \
  libv4l-dev
```

The implementation uses kernel V4L2 headers directly. `libv4l-dev` is installed in CI to provide a predictable camera-development environment even though ThinCam does not link to libv4l conversion functions.

### 4.2 Build with the repository script

```bash
chmod +x Build/build-linux.sh
./Build/build-linux.sh
```

The script:

1. Configures `Native/linux` with Ninja in release mode.
2. Builds `libthincam.so`.
3. Copies it to `Build/Native/runtimes/linux-x64/native/`.
4. Strips unneeded symbols when `strip` is available.
5. Builds `Native/linux/tests`.
6. Runs `pixel_convert_tests`.

Expected final line:

```text
Staged .../Build/Native/runtimes/linux-x64/native/libthincam.so
```

### 4.3 Manual CMake build

```bash
cmake \
  -S Native/linux \
  -B Build/Native/artifacts/linux-x64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release

cmake --build Build/Native/artifacts/linux-x64

mkdir -p Build/Native/runtimes/linux-x64/native
cp Build/Native/artifacts/linux-x64/libthincam.so \
   Build/Native/runtimes/linux-x64/native/libthincam.so
```

Build and run native conversion tests manually:

```bash
cmake \
  -S Native/linux/tests \
  -B Build/Native/artifacts/linux-tests \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release

cmake --build Build/Native/artifacts/linux-tests
./Build/Native/artifacts/linux-tests/pixel_convert_tests
```

### 4.4 Linux ARM64

The script accepts a `RID` environment variable, but changing the RID does not configure a cross compiler:

```bash
RID=linux-arm64 ./Build/build-linux.sh
```

Run that only on a native ARM64 Linux machine, or supply a CMake toolchain file and adjust the build script for cross compilation. Merely renaming an x64 output as `linux-arm64` produces an invalid package.

### 4.5 Verify the library

```bash
file Build/Native/runtimes/linux-x64/native/libthincam.so
nm -D --defined-only Build/Native/runtimes/linux-x64/native/libthincam.so | grep ' tc_'
```

Expected ABI exports:

```text
tc_get_abi_version
tc_status_message
tc_get_permission_status
tc_request_permission
tc_enumerate_devices
tc_camera_open
tc_camera_start
tc_camera_stop
tc_camera_close
```

## 5. Windows native backend

### 5.1 Prerequisites

Install:

- Visual Studio 2022 or newer.
- Desktop development with C++ workload.
- A Windows 10/11 SDK.
- CMake support for C++.
- PowerShell.

Run the script from a Visual Studio Developer PowerShell so the C++ compiler and SDK environment are available.

### 5.2 Build x64

```powershell
./Build/build-windows.ps1
```

This configures a Visual Studio x64 CMake build, builds release configuration, and stages:

```text
Build/Native/runtimes/win-x64/native/thincam.dll
```

### 5.3 Build ARM64

```powershell
$env:RID = "win-arm64"
./Build/build-windows.ps1
Remove-Item Env:RID
```

This selects the Visual Studio `ARM64` generator platform and stages:

```text
Build/Native/runtimes/win-arm64/native/thincam.dll
```

### 5.4 Manual CMake build

For x64:

```powershell
cmake `
  -S Native/windows `
  -B Build/Native/artifacts/win-x64 `
  -A x64

cmake --build Build/Native/artifacts/win-x64 --config Release

New-Item -ItemType Directory -Force `
  Build/Native/runtimes/win-x64/native | Out-Null

Copy-Item `
  Build/Native/artifacts/win-x64/Release/thincam.dll `
  Build/Native/runtimes/win-x64/native/thincam.dll `
  -Force
```

For ARM64, replace `-A x64` with `-A ARM64` and use `win-arm64` paths.

### 5.5 Verify exports

From a Visual Studio Developer PowerShell:

```powershell
dumpbin /exports Build/Native/runtimes/win-x64/native/thincam.dll
```

Confirm all nine `tc_` ABI symbols are present.

## 6. macOS and iOS native backend

The Apple script must run on macOS with Xcode installed.

### 6.1 Prerequisites

Install:

- Xcode appropriate for the target SDKs.
- Xcode command-line tools.
- CMake.
- Ninja.

Verify:

```bash
xcode-select -p
xcrun --sdk macosx --show-sdk-path
cmake --version
ninja --version
```

With Homebrew:

```bash
brew install cmake ninja
```

### 6.2 Build all Apple native artifacts

```bash
chmod +x Build/build-macos.sh
./Build/build-macos.sh
```

The script builds:

| Target        | Architecture | Staged RID           |
| ------------- | ------------ | -------------------- |
| macOS         | arm64        | `osx-arm64`          |
| macOS         | x86_64       | `osx-x64`            |
| iOS device    | arm64        | `ios-arm64`          |
| iOS simulator | arm64        | `iossimulator-arm64` |
| iOS simulator | x86_64       | `iossimulator-x64`   |

macOS outputs are dynamic libraries. iOS outputs are static archives.

### 6.3 Build only macOS manually

Apple Silicon:

```bash
cmake \
  -S Native/apple \
  -B Build/Native/artifacts/osx-arm64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0

cmake --build Build/Native/artifacts/osx-arm64
mkdir -p Build/Native/runtimes/osx-arm64/native
cp Build/Native/artifacts/osx-arm64/libthincam.dylib \
   Build/Native/runtimes/osx-arm64/native/libthincam.dylib
```

Intel:

```bash
cmake \
  -S Native/apple \
  -B Build/Native/artifacts/osx-x64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES=x86_64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0

cmake --build Build/Native/artifacts/osx-x64
mkdir -p Build/Native/runtimes/osx-x64/native
cp Build/Native/artifacts/osx-x64/libthincam.dylib \
   Build/Native/runtimes/osx-x64/native/libthincam.dylib
```

### 6.4 Build iOS device manually

```bash
cmake \
  -S Native/apple \
  -B Build/Native/artifacts/ios-arm64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_SYSROOT=iphoneos \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0

cmake --build Build/Native/artifacts/ios-arm64
mkdir -p Build/Native/runtimes/ios-arm64/native
cp Build/Native/artifacts/ios-arm64/libthincam.a \
   Build/Native/runtimes/ios-arm64/native/libthincam.a
```

### 6.5 Build iOS simulators manually

Apple Silicon simulator:

```bash
cmake \
  -S Native/apple \
  -B Build/Native/artifacts/iossimulator-arm64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_SYSROOT=iphonesimulator \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0

cmake --build Build/Native/artifacts/iossimulator-arm64
```

Intel simulator:

```bash
cmake \
  -S Native/apple \
  -B Build/Native/artifacts/iossimulator-x64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_SYSROOT=iphonesimulator \
  -DCMAKE_OSX_ARCHITECTURES=x86_64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0

cmake --build Build/Native/artifacts/iossimulator-x64
```

Copy each `libthincam.a` into the matching runtime directory.

### 6.6 Verify Apple binaries

```bash
file Build/Native/runtimes/osx-arm64/native/libthincam.dylib
nm -gU Build/Native/runtimes/osx-arm64/native/libthincam.dylib | grep '_tc_'

file Build/Native/runtimes/ios-arm64/native/libthincam.a
nm -gU Build/Native/runtimes/ios-arm64/native/libthincam.a | grep '_tc_'
```

## 7. Android native backend

### 7.1 Prerequisites

Install:

- Android SDK command-line tools.
- Android NDK r26 or newer; CI uses r27d.
- CMake 3.22 or newer.
- Ninja.

The build scripts discover the NDK automatically and only fall back to the steps below when discovery fails. They probe, in order:

1. `ANDROID_NDK_HOME`, if it points at a valid NDK.
2. `$ANDROID_HOME` / `$ANDROID_SDK_ROOT` and the default SDK locations, using the newest `ndk/<version>` found.
3. The legacy `ndk-bundle` directory.

On Windows, Ninja is additionally discovered from a Visual Studio installation, so an Android build usually needs no setup at all. If no NDK is found the Windows script offers to install the pinned revision through `sdkmanager` after asking for consent. Use `-InstallNdk` to accept up front, `-NoInstall` to refuse, or `-SkipAndroid` (`--skip-android` for the shell scripts) to leave the Android libraries out entirely.

To pin the NDK explicitly, set `ANDROID_NDK_HOME` to the NDK root:

```bash
export ANDROID_NDK_HOME="$ANDROID_SDK_ROOT/ndk/27.3.13750724"
```

Confirm the toolchain file exists:

```bash
test -f "$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
```

### 7.2 Install the CI-pinned Android tools

When `sdkmanager` is available:

```bash
sdkmanager \
  "ndk;27.3.13750724" \
  "cmake;3.22.1"
```

### 7.3 Build all supported Android ABIs

```bash
chmod +x Build/build-macos.sh
./Build/build-macos.sh
```

The script builds API 24 with static libc++ for:

| Android ABI | NuGet RID       | Output          |
| ----------- | --------------- | --------------- |
| `arm64-v8a` | `android-arm64` | `libthincam.so` |
| `x86_64`    | `android-x64`   | `libthincam.so` |

### 7.4 Manual arm64 build

```bash
cmake \
  -S Native/android \
  -B Build/Native/artifacts/android-arm64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=arm64-v8a \
  -DANDROID_PLATFORM=android-24 \
  -DANDROID_STL=c++_static

cmake --build Build/Native/artifacts/android-arm64
mkdir -p Build/Native/runtimes/android-arm64/native
cp Build/Native/artifacts/android-arm64/libthincam.so \
   Build/Native/runtimes/android-arm64/native/libthincam.so
```

### 7.5 Manual x64 emulator build

```bash
cmake \
  -S Native/android \
  -B Build/Native/artifacts/android-x64 \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" \
  -DANDROID_ABI=x86_64 \
  -DANDROID_PLATFORM=android-24 \
  -DANDROID_STL=c++_static

cmake --build Build/Native/artifacts/android-x64
mkdir -p Build/Native/runtimes/android-x64/native
cp Build/Native/artifacts/android-x64/libthincam.so \
   Build/Native/runtimes/android-x64/native/libthincam.so
```

### 7.6 Verify Android binaries

Use the NDK LLVM tools:

```bash
find "$ANDROID_NDK_HOME/toolchains/llvm/prebuilt" \
  -name llvm-readelf -type f | head -n 1
```

Then:

```bash
/path/to/llvm-readelf -h \
  Build/Native/runtimes/android-arm64/native/libthincam.so

/path/to/llvm-nm -D --defined-only \
  Build/Native/runtimes/android-arm64/native/libthincam.so | grep ' tc_'
```

## 8. Managed project builds

The managed project is multi-targeted. On machines without Android/iOS workloads, override `TargetFrameworks` so MSBuild evaluates only the desktop target.

### 8.1 Restore and build desktop `net10.0`

Use this on Windows, Linux, or macOS:

```bash
dotnet restore \
  Sources/ThinCam/ThinCam.csproj \
  -p:ThinCamTargetFrameworks=net10.0

dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0 \
  -f net10.0
```

The `TargetFrameworks` override is important. Without it, the project may try to evaluate mobile targets and require unavailable workloads.

### 8.2 Build from the solution

The solution contains the capture library, optional SkiaSharp/Avalonia adapters, tests, console sample, shared Avalonia demo, and platform heads. To avoid mobile target evaluation on a desktop-only machine:

```bash
dotnet build ThinCam.slnx \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0
```

If solution-level property propagation behaves differently in a particular IDE, build the two `.csproj` files explicitly.

### 8.3 Build the console sample

First build and stage the native backend for the current desktop OS. Then:

```bash
dotnet build \
  Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj \
  -c Release
```

Run from the source tree with the staged native directory on the platform loader path.

Linux x64:

```bash
LD_LIBRARY_PATH="$PWD/Build/Native/runtimes/linux-x64/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet run \
    --project Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj \
    -c Release
```

Windows x64:

```powershell
$native = (Resolve-Path "Build/Native/runtimes/win-x64/native").Path
$env:PATH = "$native;$env:PATH"
dotnet run `
  --project Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj `
  -c Release
```

macOS Apple Silicon:

```bash
DYLD_LIBRARY_PATH="$PWD/Build/Native/runtimes/osx-arm64/native${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}" \
  dotnet run \
    --project Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj \
    -c Release
```

Use `osx-x64` on Intel macOS. The explicit loader path is needed for a direct source `ProjectReference`; NuGet runtime asset selection applies when the library is consumed from a packed package.

The sample opens the default camera, prints frame metadata for ten seconds, and disposes every frame. It is `net10.0` and intended for desktop platforms, not Android or iOS.

### 8.4 Build the Android managed target

Install the workload:

```bash
dotnet workload install android
```

Build the library target:

```bash
dotnet restore \
  Sources/ThinCam/ThinCam.csproj \
  -p:ThinCamTargetFrameworks=net10.0-android

dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android \
  -f net10.0-android
```

The native `.so` files must already be staged. `Build/ThinCam.targets` includes them as `AndroidNativeLibrary` items so a consuming Android app places them in the APK/AAB.

Building the library project does not create a runnable Android application. A consuming app must target Android, declare the camera permission, and call the `Activity` permission overload.

### 8.5 Build the iOS managed target

Run on macOS with Xcode and the iOS workload:

```bash
dotnet workload install ios
```

Simulator on Apple Silicon:

```bash
dotnet restore \
  Sources/ThinCam/ThinCam.csproj \
  -p:ThinCamTargetFrameworks=net10.0-ios

dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64
```

Intel simulator:

```bash
dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-x64
```

iOS device:

```bash
dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r ios-arm64
```

`Build/ThinCam.targets` selects the matching static `libthincam.a`, marks it as a C++ native reference, force-loads it, and links the required Apple frameworks.

A consuming iOS app must include `NSCameraUsageDescription` and valid signing/provisioning settings for device deployment.

### 8.6 Build the macOS managed library

macOS desktop uses the ordinary `net10.0` target:

```bash
dotnet build \
  Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0 \
  -f net10.0
```

At application publish time, choose `osx-arm64` or `osx-x64` so the matching dylib runtime asset is selected.

### 8.7 Build and run the Avalonia demo

The reusable preview package and demo are separate projects. Build the reusable control for desktop with:

```bash
dotnet build \
  Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0 \
  -f net10.0
```

#### Linux desktop

Build and stage the native camera backend first, then run the desktop head:

```bash
./Build/build-linux.sh

dotnet run \
  --project Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj \
  -c Release \
  -r linux-x64
```

Use `linux-arm64` on ARM64. The desktop head explicitly copies the staged source-tree ThinCam library for its selected RID. It also references the Linux SkiaSharp native asset package required by Avalonia/Skia rendering.

#### Windows desktop

From a Visual Studio Developer PowerShell:

```powershell
./Build/build-windows.ps1

dotnet run `
  --project Samples/ThinCamDemo.Desktop/ThinCamDemo.Desktop.csproj `
  -c Release `
  -r win-x64
```

Use `win-arm64` after setting `$env:RID = "win-arm64"` before the native build.

#### macOS desktop

A camera-enabled macOS application needs a real application bundle containing `NSCameraUsageDescription`. After `./Build/build-macos.sh`, use the helper:

```bash
./Build/run-demo-macos.sh
```

The helper selects `osx-arm64` or `osx-x64`, publishes the desktop head, creates `ThinCam Demo.app`, copies the supplied `Info.plist`, ad-hoc signs the bundle, and opens it. For distribution, replace ad-hoc signing with your normal signing/notarization process.

#### Android

```bash
export ANDROID_NDK_HOME="$ANDROID_SDK_ROOT/ndk/27.3.13750724"
./Build/build-macos.sh
dotnet workload install android

dotnet build \
  Samples/ThinCamDemo.Android/ThinCamDemo.Android.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-android
```

Install/run through your normal .NET Android tooling or IDE. The Android head declares `android.permission.CAMERA` and supplies its current `Activity` to the shared permission service.

#### iOS

On macOS after `./Build/build-macos.sh` and `dotnet workload install ios`:

```bash
dotnet build \
  Samples/ThinCamDemo.iOS/ThinCamDemo.iOS.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -r iossimulator-arm64
```

Use `iossimulator-x64` for an Intel simulator or `ios-arm64` with valid signing for a physical device. The iOS head contains `NSCameraUsageDescription` and the static ThinCam native library is selected by `Build/ThinCam.targets`.

#### Adapter tests

```bash
dotnet test Tests/ThinCamTests.SkiaSharp/ThinCamTests.SkiaSharp.csproj -c Release
dotnet test Tests/ThinCamTests.Avalonia/ThinCamTests.Avalonia.csproj -c Release
```

See [Avalonia.md](Avalonia.md) for the reusable control architecture, UI threading, demo features, and platform details.

## 9. Publishing a desktop consumer

ThinCam itself is a library. There are two different cases for native asset handling.

### 9.1 Consumer uses the ThinCam NuGet package

NuGet selects the matching desktop runtime asset from `runtimes/<rid>/native` when the consuming application is restored and published with an explicit runtime identifier.

Examples:

Linux x64:

```bash
dotnet publish MyApp.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained false
```

Windows x64:

```powershell
dotnet publish MyApp.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false
```

macOS Apple Silicon:

```bash
dotnet publish MyApp.csproj \
  -c Release \
  -r osx-arm64 \
  --self-contained false
```

Inspect the publish directory and confirm that `thincam.dll`, `libthincam.so`, or `libthincam.dylib` is present.

### 9.2 Consumer uses a source `ProjectReference`

A source project reference does not consume the NuGet `runtimes` asset graph. Keep the staged native directory on the loader path, or copy the matching native file into the application output/publish directory.

Example after publishing the included sample for Linux x64:

```bash
dotnet publish \
  Samples/ThinCamDemo.Console/ThinCamDemo.Console.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained false

publish_dir="Samples/ThinCamDemo.Console/bin/Release/net10.0/linux-x64/publish"
cp Build/Native/runtimes/linux-x64/native/libthincam.so "$publish_dir/"
```

Use the corresponding native file and RID on Windows or macOS. Testing the packed package in a clean consumer project is the best verification of release asset selection.

## 10. NuGet packaging

### 10.1 Critical packaging rule

`dotnet pack` includes whatever native files currently exist under `Build/Native/runtimes`.

It does **not** build missing native platforms automatically. Packing on Linux after building only Linux creates a package that lacks Windows, Apple, and Android assets.

Before a full release package, stage every supported native artifact and verify the runtime tree.

### 10.2 Pack with the script

Package versioning is driven by [MinVer](https://github.com/adamralph/minver) from git tags (prefix `v`). Create a tag like `v0.4.2` to produce a release package, or run without a tag for a pre-release (`0.1.0-dev.N`).

```bash
chmod +x Build/build-macos.sh
./Build/build-macos.sh
```

Output:

```text
Build/Native/artifacts/packages/ThinCam.0.1.0.216.nupkg
Build/Native/artifacts/packages/ThinCam.SkiaSharp.0.1.0.216.nupkg
Build/Native/artifacts/packages/ThinCam.Avalonia.0.1.0.216.nupkg
```

To override the version explicitly, set `VERSION`:

```bash
VERSION=0.4.2 ./Build/build-macos.sh
```

### 10.3 Pack directly

```bash
dotnet pack Sources/ThinCam/ThinCam.csproj \
  -c Release -o Build/Native/artifacts/packages

dotnet pack Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj \
  -c Release -o Build/Native/artifacts/packages

dotnet pack Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj \
  -c Release -o Build/Native/artifacts/packages
```

Packing the multi-target project requires the Android and iOS workloads because all target frameworks are evaluated. For a complete cross-platform package, perform the final pack on macOS with both workloads installed and with all native artifacts staged.

NuGet expands the platform target frameworks to the SDK API levels used for compilation, such as `net10.0-android36.0` and `net10.0-ios26.0`. These remain .NET 10 assemblies; the separate folders are required for Android permission APIs and iOS static-link metadata. `SupportedOSPlatformVersion` controls the lower deployment requirement independently.

### 10.4 Inspect package contents

A `.nupkg` is a ZIP file:

```bash
unzip -l Build/Native/artifacts/packages/ThinCam.0.1.0.nupkg
```

Check for:

- `lib/net10.0/ThinCam.dll`
- `lib/net10.0-android*/ThinCam.dll`
- `lib/net10.0-ios*/ThinCam.dll`
- `runtimes/<rid>/native/...`
- `buildTransitive/ThinCam.targets`
- Package `README.md`

### 10.5 Local package test

Create a temporary NuGet source:

```bash
mkdir -p artifacts/local-feed
cp Build/Native/artifacts/packages/ThinCam.0.1.0.nupkg artifacts/local-feed/
```

In a test application:

```bash
dotnet add package ThinCam \
  --version 0.1.0 \
  --source /absolute/path/to/ThinCam/artifacts/local-feed
```

Publish for the target RID and verify the native library is copied.

## 11. CI-equivalent commands

The CI pipeline (`.github/workflows/build.yml`) runs the following jobs.

### Test (Ubuntu)

```bash
dotnet test Tests/ThinCamTests.SkiaSharp/ThinCamTests.SkiaSharp.csproj -c Release
dotnet test Tests/ThinCamTests.Avalonia/ThinCamTests.Avalonia.csproj -c Release
```

### Native Linux + Android (Ubuntu)

```bash
sudo apt-get update
sudo apt-get install -y ninja-build build-essential linux-libc-dev

# Android NDK (r27d or newer)
sdkmanager "ndk;27.3.13750724"
export ANDROID_NDK_HOME="$ANDROID_SDK_ROOT/ndk/27.3.13750724"

# Build Linux and Android native libraries (skips packaging)
./Build/build-linux.sh --skip-pack
```

### Native Windows (Windows)

```powershell
# Build Windows native libraries only (Android skipped in CI to avoid
# duplicate artifacts with the Linux job)
./Build/build-windows.ps1 -SkipPack -SkipAndroid
```

### Native Apple (macOS)

```bash
brew install ninja

# Build macOS and iOS native libraries (Android skipped in CI)
./Build/build-macos.sh --skip-pack --skip-android

dotnet workload install ios

dotnet build Sources/ThinCam/ThinCam.csproj \
  -c Release \
  -p:ThinCamTargetFrameworks=net10.0-ios \
  -f net10.0-ios \
  -r iossimulator-arm64

dotnet build Samples/ThinCamDemo.iOS/ThinCamDemo.iOS.csproj \
  -c Release -p:ThinCamTargetFrameworks=net10.0-ios -r iossimulator-arm64
```

### Package (macOS — requires all native artifacts + Xcode)

```bash
# Merge all native artifacts into Build/Native/runtimes/ then pack.
# Versioning is MinVer-driven from git tags (prefix v).
dotnet workload install android ios

dotnet pack Sources/ThinCam/ThinCam.csproj -c Release -o Build/Native/artifacts/packages
dotnet pack Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj -c Release -o Build/Native/artifacts/packages
dotnet pack Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj -c Release -o Build/Native/artifacts/packages
```

On `v*` tag pushes, CI also creates a GitHub Release with the `.nupkg` and `.snupkg` files attached.

## 12. Clean builds

Remove generated native and managed outputs:

```bash
rm -rf Build/Native/artifacts
find Sources Samples -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
```

Do not delete staged runtime libraries unless you intend to rebuild them:

```bash
find Build/Native/runtimes -type f -delete
```

The repository currently contains a staged Linux x64 library. A fully clean source release may choose not to commit generated binaries and instead produce them in release automation.

## 13. Build troubleshooting

### `A compatible installed .NET SDK ... was not found`

Install a .NET 10 SDK (10.0.300 or newer). The repository does not pin a specific SDK version.

### Android or iOS workload errors during a desktop build

Override the multi-target list:

```bash
dotnet build Sources/ThinCam/ThinCam.csproj \
  -p:ThinCamTargetFrameworks=net10.0 \
  -f net10.0
```

### `DllNotFoundException: thincam`

The native asset was not staged, not selected for the current runtime identifier, or not copied to the output directory.

Check:

```bash
find Build/Native/runtimes -type f -print
```

Then publish the consuming app with an explicit RID.

### ABI mismatch

Managed code reports an ABI error when the staged native library was built from an incompatible header/version. Delete old artifacts and rebuild the native backend from the same checkout as the managed code.

### Linux permission denied

Check device nodes and ownership:

```bash
ls -l /dev/video*
id
```

For containers, pass the device explicitly, for example:

```bash
docker run --device=/dev/video0 ...
```

Exact group and udev configuration is distribution-specific.

### Linux `FormatNotSupported`

The current backend accepts YUYV or UYVY. Inspect formats with a V4L2 utility such as `v4l2-ctl --list-formats-ext`. An MJPEG-only camera is not supported.

### Windows camera is busy or access denied

Close applications that may own the camera. Check Windows camera privacy settings. Confirm the application architecture matches the staged DLL architecture.

### Apple link errors for `tc_*` symbols

Confirm the correct `libthincam.a` exists under the exact iOS runtime identifier. Build with an explicit `-r` so `ThinCam.targets` selects the matching `NativeReference`.

### Android `UnsatisfiedLinkError`

Confirm:

- The app architecture is arm64-v8a or x86_64.
- The matching `.so` is staged.
- The consuming project imported `buildTransitive/ThinCam.targets` through the package or the source project import.
- The APK contains `lib/<abi>/libthincam.so`.

### Android permission remains denied

Confirm the manifest contains `android.permission.CAMERA` and call `CameraPermissions.RequestAsync(Activity)`. The parameterless overload cannot display the prompt.

### Avalonia preview is blank or Skia fails to load

Confirm that the application references the correct SkiaSharp native assets for its deployment. The source desktop demo includes `SkiaSharp.NativeAssets.Linux` on Linux. Also confirm that the native ThinCam library for the selected RID was staged before building the demo. A blank placeholder with no error usually means no frame has been published; inspect the demo Diagnostics tab and `Camera.LastError`.

### macOS camera prompt does not appear

Run the desktop demo as an application bundle containing `NSCameraUsageDescription`; a loose executable does not provide the required purpose string reliably. Use `Build/run-demo-macos.sh` for source testing. Reset camera privacy state in macOS settings or with the platform privacy tools when retesting denial/allow flows.
