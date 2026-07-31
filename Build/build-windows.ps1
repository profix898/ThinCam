$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$StageRoot = Join-Path $Root "Build/Native/runtimes"

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
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
    $bashExe = (Get-Command bash -ErrorAction SilentlyContinue).Source
    if (-not $bashExe) {
        Write-Host "=== Skipping Android (bash not found on PATH) ===" -ForegroundColor Yellow
        return
    }
    if (-not $env:ANDROID_NDK_HOME) {
        Write-Host "=== Skipping Android (ANDROID_NDK_HOME not set) ===" -ForegroundColor Yellow
        return
    }
    Write-Host "=== Building Android native libraries ===" -ForegroundColor Cyan
    & $bashExe "$PSScriptRoot\build-android.sh"
    if ($LASTEXITCODE -ne 0) { throw "Android build failed." }
}

function Build-LinuxNative {
    $wslExe = (Get-Command wsl -ErrorAction SilentlyContinue).Source
    if (-not $wslExe) {
        Write-Host "=== Skipping Linux (WSL not available) ===" -ForegroundColor Yellow
        return
    }
    Write-Host "=== Building Linux native libraries (WSL) ===" -ForegroundColor Cyan
    & $wslExe bash -c "cd '$($Root -replace '\\','/')' && ./Build/build-linux.sh"
    if ($LASTEXITCODE -ne 0) { throw "Linux build failed." }
}

function Pack-Packages {
    Write-Host "=== Packaging NuGet packages ===" -ForegroundColor Cyan
    $PackArgs = @("-c", "Release", "-o", (Join-Path $Root "artifacts/packages"))
    if ($env:VERSION) { $PackArgs += "-p:PackageVersion=$env:VERSION" }
    if ($env:TARGET_FRAMEWORKS) { $PackArgs += "-p:ThinCamTargetFrameworks=$env:TARGET_FRAMEWORKS" }
    Invoke-Checked dotnet @("pack", (Join-Path $Root "Sources/ThinCam/ThinCam.csproj"), $PackArgs)
    Invoke-Checked dotnet @("pack", (Join-Path $Root "Sources/ThinCam.SkiaSharp/ThinCam.SkiaSharp.csproj"), $PackArgs)
    Invoke-Checked dotnet @("pack", (Join-Path $Root "Sources/ThinCam.Avalonia/ThinCam.Avalonia.csproj"), $PackArgs)
}

Build-WindowsNative -Rid "win-x64" -Arch "x64"
Build-WindowsNative -Rid "win-arm64" -Arch "ARM64"
Build-AndroidNative
Build-LinuxNative
Pack-Packages

Write-Host "=== Done ===" -ForegroundColor Green
