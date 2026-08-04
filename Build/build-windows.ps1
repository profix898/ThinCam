param([switch]$SkipPack)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$StageRoot = Join-Path $Root "Build/Native/runtimes"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(ValueFromRemainingArguments)][object[]]$Arguments
    )
    # Flatten any nested arrays so callers can compose argument lists freely.
    $flat = @($Arguments | ForEach-Object { $_ } | Where-Object { $null -ne $_ })
    & $Executable @flat
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($flat -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Build-WindowsNative {
    param([string]$Rid, [string]$Arch)
    $BuildDir = Join-Path $Root "artifacts/$Rid"
    $StageDir = Join-Path $StageRoot "$Rid/native"
    Write-Host "=== Building $Rid ===" -ForegroundColor Cyan
    Invoke-Checked cmake @("-S", (Join-Path $Root "Native/windows"), "-B", $BuildDir, "-A", $Arch)
    Invoke-Checked cmake @("--build", $BuildDir, "--config", "Release")
    New-Item -ItemType Directory -Force $StageDir | Out-Null
    Copy-Item (Join-Path $BuildDir "Release/thincam.dll") (Join-Path $StageDir "thincam.dll") -Force
    Write-Host "Staged $StageDir/thincam.dll"
}

function Build-AndroidNative {
    if (-not $env:ANDROID_NDK_HOME) {
        Write-Host "=== Skipping Android (ANDROID_NDK_HOME not set) ===" -ForegroundColor Yellow
        return
    }
    if (-not (Get-Command ninja -ErrorAction SilentlyContinue)) {
        Write-Host "=== Skipping Android (ninja not found on PATH; required by the NDK toolchain) ===" -ForegroundColor Yellow
        return
    }
    $toolchain = "$env:ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
    $abis = @(
        @{ Abi = "arm64-v8a"; Rid = "android-arm64" },
        @{ Abi = "x86_64"; Rid = "android-x64" }
    )
    foreach ($entry in $abis) {
        $buildDir = Join-Path $Root "artifacts/$($entry.Rid)"
        $stageDir = Join-Path $StageRoot "$($entry.Rid)/native"
        Write-Host "=== Building Android $($entry.Abi) ===" -ForegroundColor Cyan
        Invoke-Checked cmake @(
            "-S", (Join-Path $Root "Native/android"),
            "-B", $buildDir,
            "-G", "Ninja",
            "-DCMAKE_BUILD_TYPE=Release",
            "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
            "-DANDROID_ABI=$($entry.Abi)",
            "-DANDROID_PLATFORM=android-24",
            "-DANDROID_STL=c++_static"
        )
        Invoke-Checked cmake @("--build", $buildDir)
        New-Item -ItemType Directory -Force $stageDir | Out-Null
        Copy-Item (Join-Path $buildDir "libthincam.so") (Join-Path $stageDir "libthincam.so") -Force
        Write-Host "Staged $stageDir/libthincam.so"
    }
}

function Build-LinuxNative {
    $wslExe = (Get-Command wsl -ErrorAction SilentlyContinue).Source
    if (-not $wslExe) {
        Write-Host "=== Skipping Linux (WSL not available) ===" -ForegroundColor Yellow
        return
    }
    $distros = (& $wslExe -l -q 2>$null) -join "" -replace "`0", ""
    if ($LASTEXITCODE -ne 0 -or -not $distros.Trim()) {
        Write-Host "=== Skipping Linux (no WSL distribution installed) ===" -ForegroundColor Yellow
        return
    }

    # Translate the Windows repository path into its WSL mount point.
    $rootFwd = $Root -replace '\\', '/'
    $wslRoot = (& $wslExe bash -c "wslpath -a '$rootFwd'" 2>$null) -join "" -replace "`0", ""
    $wslRoot = $wslRoot.Trim()
    if ($LASTEXITCODE -ne 0 -or -not $wslRoot) {
        Write-Host "=== Skipping Linux (could not resolve WSL path for $Root) ===" -ForegroundColor Yellow
        return
    }

    & $wslExe bash -lc "command -v cmake >/dev/null && command -v ninja >/dev/null" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "=== Skipping Linux (cmake and/or ninja not installed in WSL) ===" -ForegroundColor Yellow
        Write-Host "    Install with: wsl sudo apt-get install -y cmake ninja-build build-essential" -ForegroundColor Yellow
        return
    }

    Write-Host "=== Building Linux native libraries (WSL) ===" -ForegroundColor Cyan
    & $wslExe bash -lc "cd '$wslRoot' && ./Build/build-linux.sh --skip-pack"
    if ($LASTEXITCODE -ne 0) { throw "Linux build failed." }
}

function Pack-Packages {
    Write-Host "=== Packaging NuGet packages ===" -ForegroundColor Cyan
    $PackArgs = @("-c", "Release", "-o", (Join-Path $Root "artifacts/packages"))
    if ($env:TARGET_FRAMEWORKS) { $PackArgs += "-p:ThinCamTargetFrameworks=$env:TARGET_FRAMEWORKS" }
    $projects = @(
        "Sources/ThinCam/ThinCam.csproj",
        "Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj",
        "Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj"
    )
    foreach ($proj in $projects) {
        Invoke-Checked dotnet (@("pack", (Join-Path $Root $proj)) + $PackArgs)
    }
}

Build-WindowsNative -Rid "win-x64" -Arch "x64"
Build-WindowsNative -Rid "win-arm64" -Arch "ARM64"
Build-AndroidNative
Build-LinuxNative
if (-not $SkipPack) { Pack-Packages }

Write-Host "=== Done ===" -ForegroundColor Green
exit 0
