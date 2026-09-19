# Builds a Windows player from the compile-check mirror, runs it windowed, screenshots its window,
# and closes it. Use to eyeball UI changes without touching the open editor.
#
# Usage:  powershell -File tools\screenshot-player.ps1 [-OutFile path.png] [-SkipBuild] [-WaitSeconds 12]
# Runs tools\compile-check.ps1 first (mirror + compile), then a batchmode player build (~2-3 min),
# unless -SkipBuild is given and a previous build exists.

param(
    [string]$OutFile = "$PSScriptRoot\..\..\arcade-launcher-compilecheck\launcher-screenshot.png",
    [switch]$SkipBuild,
    [int]$WaitSeconds = 12
)

$ErrorActionPreference = 'Stop'

$UnityVersion = '6000.4.2f1'
$UnityExe = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$CheckRoot = "$ProjectRoot-compilecheck"
$PlayerExe = Join-Path $CheckRoot 'Build\ArcadeLauncher.exe'
$BuildLog = Join-Path $CheckRoot 'build.log'
$WindowWidth = 1600
$WindowHeight = 900

if (-not $SkipBuild -or -not (Test-Path $PlayerExe)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'compile-check.ps1')
    if ($LASTEXITCODE -ne 0) { throw "compile-check failed; not building a player." }

    Write-Host "Building Windows player into $PlayerExe ..."
    $build = Start-Process -FilePath $UnityExe -PassThru -Wait -ArgumentList @(
        '-batchmode', '-nographics', '-quit',
        '-projectPath', $CheckRoot,
        '-buildWindows64Player', $PlayerExe,
        '-logFile', $BuildLog
    )
    if ($build.ExitCode -ne 0) { throw "Player build failed (exit $($build.ExitCode)). See $BuildLog" }
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class ScreenshotNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
# Without this, GetWindowRect reports scaled coordinates on a HiDPI display and the bitmap is the wrong size.
[ScreenshotNative]::SetProcessDPIAware() | Out-Null
# PW_RENDERFULLCONTENT: ask DWM for the window's own composed surface, so the capture is correct even
# when another window (typically the Unity editor) is in front, and no focus is stolen from the user.
$PrintWindowRenderFullContent = 0x2

$player = Start-Process -FilePath $PlayerExe -PassThru -ArgumentList @(
    '-screen-fullscreen', '0', '-screen-width', $WindowWidth, '-screen-height', $WindowHeight)
try {
    Start-Sleep -Seconds $WaitSeconds
    $player.Refresh()
    $handle = $player.MainWindowHandle

    $rect = New-Object ScreenshotNative+RECT
    [ScreenshotNative]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $width = $rect.R - $rect.L
    $height = $rect.B - $rect.T

    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $deviceContext = $graphics.GetHdc()
    $printed = [ScreenshotNative]::PrintWindow($handle, $deviceContext, $PrintWindowRenderFullContent)
    $graphics.ReleaseHdc($deviceContext)
    if (-not $printed) { throw "PrintWindow failed for the player window." }
    $resolved = [System.IO.Path]::GetFullPath($OutFile)
    $bitmap.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Host "Saved $resolved (${width}x${height})"
}
finally {
    Stop-Process -Id $player.Id -Force -Confirm:$false
}
