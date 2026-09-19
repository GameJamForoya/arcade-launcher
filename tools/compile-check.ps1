# Compile-checks the working tree with the Unity CLI without touching the open editor.
#
# Unity refuses to run batchmode on a project another editor instance already has open, so this
# mirrors Assets/Packages/ProjectSettings into a sibling folder and compiles that copy instead.
# The copy keeps its Library between runs, so the first run takes a few minutes (full import)
# and later runs take under a minute.
#
# Usage:  powershell -File tools\compile-check.ps1
# Exit code is 0 when every project assembly compiled with no "error CS" lines, 1 otherwise.

$ErrorActionPreference = 'Stop'

$UnityVersion = '6000.4.2f1'
$UnityExe = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$CheckRoot = "$ProjectRoot-compilecheck"
$LogPath = Join-Path $CheckRoot 'compile.log'
$MirroredFolders = @('Assets', 'Packages', 'ProjectSettings')
$RobocopyFilesCopiedOrLess = 1

if (-not (Test-Path $UnityExe)) {
    Write-Error "Unity $UnityVersion not found at $UnityExe. Update `$UnityVersion in this script."
}

New-Item -ItemType Directory -Force $CheckRoot | Out-Null
foreach ($folder in $MirroredFolders) {
    # /MIR keeps the copy identical, so deletions in the working tree are mirrored too.
    robocopy (Join-Path $ProjectRoot $folder) (Join-Path $CheckRoot $folder) /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt $RobocopyFilesCopiedOrLess) {
        Write-Error "robocopy failed for $folder with exit code $LASTEXITCODE"
    }
}

Write-Host "Compiling copy at $CheckRoot ..."
$unity = Start-Process -FilePath $UnityExe -PassThru -Wait -ArgumentList @(
    '-batchmode', '-nographics', '-quit',
    '-projectPath', $CheckRoot,
    '-logFile', $LogPath
)

# Only errors from project code count. Unity's own package cache can report transient errors on
# the first import pass that its later passes resolve.
$projectErrors = Select-String -Path $LogPath -Pattern 'error CS' |
    Where-Object { $_.Line -notmatch 'Library[\\/]PackageCache' }

if ($projectErrors) {
    Write-Host "COMPILE FAILED ($($projectErrors.Count) errors):" -ForegroundColor Red
    $projectErrors | ForEach-Object { Write-Host "  $($_.Line.Trim())" }
    exit 1
}

if ($unity.ExitCode -ne 0) {
    Write-Host "Unity exited with code $($unity.ExitCode). See $LogPath" -ForegroundColor Red
    exit 1
}

Write-Host "COMPILE OK - no project errors. Log: $LogPath" -ForegroundColor Green
exit 0
