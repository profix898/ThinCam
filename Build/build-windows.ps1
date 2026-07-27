$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Rid = if ($env:RID) { $env:RID } else { "win-x64" }
$Arch = if ($Rid -eq "win-arm64") { "ARM64" } else { "x64" }
$BuildDir = Join-Path $Root "artifacts/$Rid"
$StageDir = Join-Path $Root "Sources/ThinCam/runtimes/$Rid/native"

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Invoke-Checked cmake @("-S", (Join-Path $Root "Native/windows"), "-B", $BuildDir, "-A", $Arch)
Invoke-Checked cmake @("--build", $BuildDir, "--config", "Release")

New-Item -ItemType Directory -Force $StageDir | Out-Null
Copy-Item (Join-Path $BuildDir "Release/thincam.dll") (Join-Path $StageDir "thincam.dll") -Force
Write-Host "Staged $StageDir/thincam.dll"
