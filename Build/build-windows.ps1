<#
.SYNOPSIS
    Builds the ThinCam native libraries on Windows and packs the NuGet packages.

.DESCRIPTION
    Builds the Windows native libraries (MSVC), the Android native libraries
    (NDK, if available) and optionally the Linux native libraries through WSL,
    then packs the managed NuGet packages.

    Ninja and the Android NDK are auto-discovered from a Visual Studio or
    Android Studio installation. If the NDK is missing it can be installed on
    request through the Android SDK command-line tools.

.PARAMETER SkipPack
    Build the native libraries only; do not produce NuGet packages.

.PARAMETER InstallNdk
    Install a missing Android NDK without prompting. Implies consent to the
    Android SDK licence terms.

.PARAMETER NoInstall
    Never install anything; skip the Android build if the NDK is missing.

.PARAMETER SkipAndroid
    Do not build the Android native libraries even if an NDK is available.
#>
param(
    [switch]$SkipPack,
    [switch]$InstallNdk,
    [switch]$NoInstall,
    [switch]$SkipAndroid
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$StageRoot = Join-Path $Root "Build/Native/runtimes"
$Artifacts = Join-Path $Root "Build/Native/artifacts"

# NDK revision installed on request. Matches the LTS used by CI (r27d).
# Local builds use auto-discovery and will prefer the newest installed NDK.
$PinnedNdkVersion = "27.3.13750724"   # r27d (2024 LTS)

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

function Test-Interactive {
    if ($env:CI -eq "true") { return $false }
    if ($NoInstall) { return $false }
    return [Environment]::UserInteractive
}

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# --------------------------------------------------------------------------
# Toolchain discovery
# --------------------------------------------------------------------------

function Resolve-Ninja {
    if (Get-Command ninja -ErrorAction SilentlyContinue) { return $true }

    $roots = @(
        "${env:ProgramFiles}\Microsoft Visual Studio",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio"
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $roots) {
        $found = Get-ChildItem $root -Filter ninja.exe -Recurse -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) {
            $env:PATH = "$($found.Directory.FullName);$env:PATH"
            Write-Host "Using ninja from $($found.FullName)" -ForegroundColor DarkGray
            return $true
        }
    }

    $sdk = Find-AndroidSdk
    if ($sdk) {
        $found = Get-ChildItem (Join-Path $sdk "cmake") -Filter ninja.exe -Recurse -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) {
            $env:PATH = "$($found.Directory.FullName);$env:PATH"
            Write-Host "Using ninja from $($found.FullName)" -ForegroundColor DarkGray
            return $true
        }
    }
    return $false
}

function Find-AndroidSdk {
    $candidates = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        "$env:LOCALAPPDATA\Android\Sdk",
        "${env:ProgramFiles(x86)}\Android\android-sdk",
        "${env:ProgramFiles}\Android\android-sdk"
    ) | Where-Object { $_ -and (Test-Path $_) }
    return $candidates | Select-Object -First 1
}

function Test-NdkRoot {
    param([string]$Path)
    return $Path -and (Test-Path (Join-Path $Path "build/cmake/android.toolchain.cmake"))
}

function Get-NdkRevision {
    param([string]$Path)
    $props = Join-Path $Path "source.properties"
    if (-not (Test-Path $props)) { return "unknown" }
    $line = Select-String -Path $props -Pattern '^Pkg\.Revision\s*=\s*(.+)$' | Select-Object -First 1
    if ($line) { return $line.Matches[0].Groups[1].Value.Trim() }
    return "unknown"
}

function Install-AndroidNdk {
    param([string]$Sdk)

    $sdkManager = Join-Path $Sdk "cmdline-tools/latest/bin/sdkmanager.bat"
    if (-not (Test-Path $sdkManager)) {
        $sdkManager = Get-ChildItem (Join-Path $Sdk "cmdline-tools") -Filter sdkmanager.bat -Recurse -ErrorAction SilentlyContinue |
                      Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $sdkManager) {
        Write-Host "    sdkmanager not found. Install 'Android SDK Command-line Tools' via" -ForegroundColor Yellow
        Write-Host "    Android Studio > Tools > SDK Manager > SDK Tools." -ForegroundColor Yellow
        return $null
    }

    if (-not $InstallNdk) {
        Write-Host ""
        Write-Host "The Android NDK is required to build the Android native libraries." -ForegroundColor Yellow
        Write-Host "  SDK        : $Sdk"
        Write-Host "  NDK version: $PinnedNdkVersion"
        Write-Host "  Installer  : $sdkManager"
        if (-not (Test-Elevated) -and $Sdk -like "$([Environment]::GetFolderPath('ProgramFilesX86'))*") {
            Write-Host "  NOTE: this SDK lives under Program Files and may require an elevated shell." -ForegroundColor Yellow
        }
        $answer = Read-Host "Install it now? Android SDK licence terms must be accepted [y/N]"
        if ($answer -notmatch '^(y|yes)$') {
            Write-Host "    Installation declined." -ForegroundColor Yellow
            return $null
        }
    }

    Write-Host "=== Accepting Android SDK licences ===" -ForegroundColor Cyan
    & $sdkManager --licenses
    Write-Host "=== Installing ndk;$PinnedNdkVersion ===" -ForegroundColor Cyan
    & $sdkManager --install "ndk;$PinnedNdkVersion"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "    sdkmanager failed with exit code $LASTEXITCODE." -ForegroundColor Yellow
        return $null
    }

    $installed = Join-Path $Sdk "ndk/$PinnedNdkVersion"
    if (Test-NdkRoot $installed) {
        Write-Host "Installed NDK at $installed" -ForegroundColor Green
        Write-Host "Persist it with:" -ForegroundColor DarkGray
        Write-Host "  [Environment]::SetEnvironmentVariable('ANDROID_NDK_HOME','$installed','User')" -ForegroundColor DarkGray
        return $installed
    }
    return $null
}

function Resolve-AndroidNdk {
    # 1. Explicit environment variable wins.
    if (Test-NdkRoot $env:ANDROID_NDK_HOME) { return $env:ANDROID_NDK_HOME }
    if ($env:ANDROID_NDK_HOME) {
        Write-Host "ANDROID_NDK_HOME is set but does not look like an NDK: $env:ANDROID_NDK_HOME" -ForegroundColor Yellow
    }

    $sdk = Find-AndroidSdk
    if (-not $sdk) {
        Write-Host "    No Android SDK found. Set ANDROID_HOME or install Android Studio." -ForegroundColor Yellow
        return $null
    }

    # 2. Side-by-side versioned NDKs; prefer the pinned revision, else newest.
    $ndkDir = Join-Path $sdk "ndk"
    if (Test-Path $ndkDir) {
        $pinned = Join-Path $ndkDir $PinnedNdkVersion
        if (Test-NdkRoot $pinned) { return $pinned }
        $newest = Get-ChildItem $ndkDir -Directory -ErrorAction SilentlyContinue |
                  Where-Object { Test-NdkRoot $_.FullName } |
                  Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } -Descending |
                  Select-Object -First 1
        if ($newest) { return $newest.FullName }
    }

    # 3. Legacy single-NDK layout.
    $bundle = Join-Path $sdk "ndk-bundle"
    if (Test-NdkRoot $bundle) { return $bundle }

    # 4. Nothing installed - offer to install it.
    if ($NoInstall) {
        Write-Host "    No NDK installed and -NoInstall was specified." -ForegroundColor Yellow
        return $null
    }
    if (-not (Test-Interactive) -and -not $InstallNdk) {
        Write-Host "    No NDK installed. Re-run interactively or pass -InstallNdk." -ForegroundColor Yellow
        return $null
    }
    return Install-AndroidNdk -Sdk $sdk
}

# --------------------------------------------------------------------------
# Builds
# --------------------------------------------------------------------------

function Build-WindowsNative {
    param([string]$Rid, [string]$Arch)
    $BuildDir = Join-Path $Artifacts "$Rid"
    $StageDir = Join-Path $StageRoot "$Rid/native"
    Write-Host "=== Building $Rid ===" -ForegroundColor Cyan
    Invoke-Checked cmake @("-S", (Join-Path $Root "Native/windows"), "-B", $BuildDir, "-A", $Arch)
    Invoke-Checked cmake @("--build", $BuildDir, "--config", "Release")
    New-Item -ItemType Directory -Force $StageDir | Out-Null
    Copy-Item (Join-Path $BuildDir "Release/thincam.dll") (Join-Path $StageDir "thincam.dll") -Force
    Write-Host "Staged $StageDir/thincam.dll"
}

function Get-NdkStrip {
    param([string]$Ndk)
    $prebuilt = Join-Path $Ndk "toolchains/llvm/prebuilt"
    if (-not (Test-Path $prebuilt)) { return $null }
    return Get-ChildItem $prebuilt -Filter "llvm-strip.exe" -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1 -ExpandProperty FullName
}

function Build-AndroidNative {
    if ($SkipAndroid) {
        Write-Host "=== Skipping Android (-SkipAndroid) ===" -ForegroundColor Yellow
        return
    }
    $ndk = Resolve-AndroidNdk
    if (-not $ndk) {
        Write-Host "=== Skipping Android (no NDK available) ===" -ForegroundColor Yellow
        return
    }
    if (-not (Resolve-Ninja)) {
        Write-Host "=== Skipping Android (ninja not found; required by the NDK toolchain) ===" -ForegroundColor Yellow
        return
    }
    Write-Host "Using NDK $(Get-NdkRevision $ndk) at $ndk" -ForegroundColor DarkGray

    $toolchain = (Join-Path $ndk "build/cmake/android.toolchain.cmake") -replace '\\', '/'
    $strip = Get-NdkStrip $ndk
    $abis = @(
        @{ Abi = "arm64-v8a"; Rid = "android-arm64" },
        @{ Abi = "x86_64";    Rid = "android-x64" }
    )
    foreach ($entry in $abis) {
        $buildDir = Join-Path $Artifacts "$($entry.Rid)"
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
        $target = Join-Path $stageDir "libthincam.so"
        Copy-Item (Join-Path $buildDir "libthincam.so") $target -Force
        if ($strip) {
            & $strip --strip-unneeded $target
            if ($LASTEXITCODE -ne 0) { Write-Host "    llvm-strip failed; shipping unstripped binary." -ForegroundColor Yellow }
        }
        Write-Host "Staged $target"
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
    $wslRoot = ((& $wslExe bash -c "wslpath -a '$rootFwd'" 2>$null) -join "" -replace "`0", "").Trim()
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
    # Android was already built from Windows above; skip it in WSL to avoid
    # reusing CMake caches that contain Windows paths.
    & $wslExe bash -lc "cd '$wslRoot' && ./Build/build-linux.sh --skip-pack --skip-android"
    if ($LASTEXITCODE -ne 0) { throw "Linux build failed." }
}

function Pack-Packages {
    Write-Host "=== Packaging NuGet packages ===" -ForegroundColor Cyan
    $PackArgs = @("-c", "Release", "-o", (Join-Path $Artifacts "packages"))
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
