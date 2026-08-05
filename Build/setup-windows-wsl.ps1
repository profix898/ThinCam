<#
.SYNOPSIS
    Installs the Linux build prerequisites (C/C++ toolchain, CMake, Ninja,
    .NET 10 SDK, and the Android NDK) into the default WSL distribution.

.DESCRIPTION
    Run from PowerShell on Windows.  The script detects whether the current
    WSL user has password-less sudo and, if not, opens an interactive prompt
    inside WSL so the user can enter their password for the apt-get steps.

    Everything that can be installed without sudo (CMake, Ninja, .NET, the
    Android SDK command-line tools and NDK) is installed under $HOME in the
    WSL filesystem.  Only build-essential, linux-libc-dev, default-jdk and
    (optionally) cmake/ninja via apt require sudo.

    Idempotent: re-running skips components that are already installed.

.PARAMETER NdkVersion
    NDK package revision to install via sdkmanager. Defaults to 29.0.14206865
    (r29, latest stable). Pass an empty string to skip the NDK.
#>
param(
    [string]$NdkVersion = "29.0.14206865"
)

$ErrorActionPreference = "Stop"

# Verify WSL exists.
$wslExe = (Get-Command wsl -ErrorAction SilentlyContinue).Source
if (-not $wslExe) {
    throw "WSL is not installed. Install 'Windows Subsystem for Linux' first."
}

# Verify a distribution is installed.
$distros = (& $wslExe -l -q 2>$null) -join "" -replace "`0", ""
if ($LASTEXITCODE -ne 0 -or -not $distros.Trim()) {
    throw "No WSL distribution is installed. Run 'wsl --install' first."
}

Write-Host "Using WSL distribution: $($distros.Trim())" -ForegroundColor Cyan

# Determine whether passwordless sudo is available.
& $wslExe bash -lc "sudo -n true 2>/dev/null" 2>$null
$passwordlessSudo = ($LASTEXITCODE -eq 0)

if (-not $passwordlessSudo) {
    Write-Host ""
    Write-Host "Some packages require sudo (apt-get). You will be prompted for" -ForegroundColor Yellow
    Write-Host "your WSL password inside the WSL terminal." -ForegroundColor Yellow
    Write-Host ""
}

# Build the setup script. We write it to a temp file and execute inside WSL.
$setupScript = @'
set -euo pipefail

NDK_VERSION="__NDK_VERSION__"

echo "=== WSL build prerequisite installer ==="
echo "User: $(whoami)  Host: $(uname -a)"

# ---------------------------------------------------------------------------
# sudo wrapper: if passwordless sudo is not available, acquire it once via
# `sudo -v` (which prompts) and keep the timestamp alive in the background.
# ---------------------------------------------------------------------------
SUDO=""
if ! sudo -n true 2>/dev/null; then
  echo "Enter your WSL password for sudo (apt-get packages):"
  sudo -v
  # Keep sudo timestamp alive in the background.
  ( while true; do sudo -n true; sleep 60; done 2>/dev/null ) &
  KEEPER_PID=$!
  trap "kill $KEEPER_PID 2>/dev/null || true" EXIT
  SUDO="sudo"
else
  SUDO="sudo"
fi

# ---------------------------------------------------------------------------
# apt packages: C/C++ compiler, make, kernel headers (videodev2.h), Java
# (for sdkmanager), and cmake/ninja as a fallback.
# ---------------------------------------------------------------------------
NEED_APT=false
for pkg in build-essential linux-libc-dev default-jdk; do
  if ! dpkg -s "$pkg" >/dev/null 2>&1; then
    NEED_APT=true
    break
  fi
done

if [ "$NEED_APT" = "true" ]; then
  echo "=== Installing apt packages (build-essential, linux-libc-dev, default-jdk) ==="
  $SUDO apt-get update -qq
  $SUDO apt-get install -y build-essential linux-libc-dev default-jdk
else
  echo "=== apt packages already installed ==="
fi

# Export JAVA_HOME for sdkmanager.
export JAVA_HOME="/usr/lib/jvm/default-java"
if [ ! -d "$JAVA_HOME" ]; then
  # Fallback: find any JVM.
  JAVA_HOME="$(dirname "$(dirname "$(readlink -f "$(command -v javac)")")")"
fi
echo "JAVA_HOME=$JAVA_HOME"

# ---------------------------------------------------------------------------
# CMake (userspace, no sudo needed)
# ---------------------------------------------------------------------------
CMAKE_VERSION="3.31.6"
if [ ! -x "$HOME/tools/cmake/bin/cmake" ]; then
  echo "=== Installing CMake $CMAKE_VERSION ==="
  cd /tmp
  wget -q "https://github.com/Kitware/CMake/releases/download/v${CMAKE_VERSION}/cmake-${CMAKE_VERSION}-linux-x86_64.tar.gz"
  rm -rf "$HOME/tools/cmake"
  mkdir -p "$HOME/tools/cmake"
  tar xzf "cmake-${CMAKE_VERSION}-linux-x86_64.tar.gz" -C "$HOME/tools/cmake" --strip-components=1
  rm -f "cmake-${CMAKE_VERSION}-linux-x86_64.tar.gz"
else
  echo "=== CMake already installed: $(cmake --version | head -1) ==="
fi

# ---------------------------------------------------------------------------
# Ninja (userspace, no sudo needed)
# ---------------------------------------------------------------------------
NINJA_VERSION="1.12.1"
if ! command -v ninja >/dev/null 2>&1 && [ ! -x "$HOME/.local/bin/ninja" ]; then
  echo "=== Installing Ninja $NINJA_VERSION ==="
  mkdir -p "$HOME/.local/bin"
  cd /tmp
  wget -q "https://github.com/ninja-build/ninja/releases/download/v${NINJA_VERSION}/ninja-linux.zip"
  unzip -oq ninja-linux.zip -d "$HOME/.local/bin"
  chmod +x "$HOME/.local/bin/ninja"
  rm -f ninja-linux.zip
else
  echo "=== Ninja already installed ==="
fi

# ---------------------------------------------------------------------------
# .NET 10 SDK (userspace, no sudo needed)
# ---------------------------------------------------------------------------
if [ ! -x "$HOME/.dotnet/dotnet" ]; then
  echo "=== Installing .NET 10 SDK ==="
  cd /tmp
  wget -q https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
  chmod +x dotnet-install.sh
  ./dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" --no-path
  rm -f dotnet-install.sh
else
  echo "=== .NET SDK already installed ==="
fi

# ---------------------------------------------------------------------------
# Android NDK (userspace, no sudo needed)
# ---------------------------------------------------------------------------
if [ -n "$NDK_VERSION" ]; then
  NDK_PATH="$HOME/Android/Sdk/ndk/$NDK_VERSION"
  if [ ! -f "$NDK_PATH/build/cmake/android.toolchain.cmake" ]; then
    echo "=== Installing Android SDK command-line tools ==="
    mkdir -p "$HOME/Android/Sdk/cmdline-tools"
    cd "$HOME/Android/Sdk/cmdline-tools"
    CMDLINE_ZIP="commandlinetools-linux-11076708_latest.zip"
    if [ ! -f "$CMDLINE_ZIP" ]; then
      wget -q "https://dl.google.com/android/repository/$CMDLINE_ZIP"
    fi
    unzip -oq "$CMDLINE_ZIP"
    if [ -d cmdline-tools ] && [ ! -d latest ]; then
      mv cmdline-tools latest
    fi
    rm -f "$CMDLINE_ZIP"

    export ANDROID_HOME="$HOME/Android/Sdk"
    SDKMANAGER="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"

    echo "=== Accepting Android SDK licences ==="
    yes | "$SDKMANAGER" --licenses >/dev/null 2>&1 || true

    echo "=== Installing NDK $NDK_VERSION ==="
    "$SDKMANAGER" --install "ndk;$NDK_VERSION"
  else
    echo "=== NDK $NDK_VERSION already installed ==="
  fi
else
  echo "=== Skipping Android NDK (no version specified) ==="
fi

# ---------------------------------------------------------------------------
# Persist PATH and environment variables in ~/.profile
# ---------------------------------------------------------------------------
PROFILE_MARKER="# ThinCam WSL toolchain"
if ! grep -q "$PROFILE_MARKER" "$HOME/.profile" 2>/dev/null; then
  cat >> "$HOME/.profile" <<'PROFILE_EOF'

# ThinCam WSL toolchain
export PATH="$HOME/.dotnet:$HOME/.local/bin:$HOME/tools/cmake/bin:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
export JAVA_HOME="/usr/lib/jvm/default-java"
export ANDROID_HOME="$HOME/Android/Sdk"
PROFILE_EOF
fi

# Always ensure NDK_HOME is set if the NDK exists.
NDK_PATH="$HOME/Android/Sdk/ndk/$NDK_VERSION"
if [ -f "$NDK_PATH/build/cmake/android.toolchain.cmake" ]; then
  if ! grep -q "ANDROID_NDK_HOME" "$HOME/.profile" 2>/dev/null; then
    echo "export ANDROID_NDK_HOME=\"$NDK_PATH\"" >> "$HOME/.profile"
  fi
fi

# ---------------------------------------------------------------------------
# Verify
# ---------------------------------------------------------------------------
export PATH="$HOME/.dotnet:$HOME/.local/bin:$HOME/tools/cmake/bin:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"

echo ""
echo "=== Verification ==="
echo "gcc      : $(gcc --version 2>/dev/null | head -1 || echo MISSING)"
echo "g++      : $(g++ --version 2>/dev/null | head -1 || echo MISSING)"
echo "make     : $(make --version 2>/dev/null | head -1 || echo MISSING)"
echo "cmake    : $(cmake --version 2>/dev/null | head -1 || echo MISSING)"
echo "ninja    : $(ninja --version 2>/dev/null || echo MISSING)"
echo "dotnet   : $(dotnet --version 2>/dev/null || echo MISSING)"
echo "java     : $(java -version 2>&1 | head -1 || echo MISSING)"
echo "v4l2.h   : $(test -f /usr/include/linux/videodev2.h && echo OK || echo MISSING)"
if [ -n "$NDK_VERSION" ]; then
  echo "NDK      : $(test -f "$NDK_PATH/build/cmake/android.toolchain.cmake" && echo "$NDK_PATH" || echo MISSING)"
fi
echo ""
echo "=== Done. Open a new WSL terminal or run: source ~/.profile ==="
'@ -replace "__NDK_VERSION__", $NdkVersion

# Write script to a temp location and execute in WSL.
$tmpDir = "C:\Users\thilo\AppData\Local\Temp\opencode"
New-Item -ItemType Directory -Force $tmpDir | Out-Null
$scriptPath = Join-Path $tmpDir "setup-wsl.sh"
[IO.File]::WriteAllText($scriptPath, ($setupScript -replace "`r`n", "`n"))

Write-Host "Running setup script in WSL..." -ForegroundColor Cyan
& $wslExe bash -lc "bash /mnt/c/Users/thilo/AppData/Local/Temp/opencode/setup-wsl.sh"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Setup failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "WSL setup complete." -ForegroundColor Green
Write-Host "Open a new WSL terminal or run 'source ~/.profile' to pick up the new PATH." -ForegroundColor DarkGray
