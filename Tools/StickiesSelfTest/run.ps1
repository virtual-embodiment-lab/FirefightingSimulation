<#
.SYNOPSIS
  Runs the Stickies wall-haptics self-test in a headless Unity, with no hardware.

.DESCRIPTION
  Copies the Stickies scripts and Assets/Scripts/Editor/StickiesSelfTest.cs into a small
  throwaway Unity project under %TEMP% (with stand-ins for VRIK, Normcore and XR Origin from
  Stubs.cs), runs StickiesSelfTest.RunBatch there, and prints the PASS/FAIL lines.
  It never opens this project, so it works while the Editor is open. The first run takes about a
  minute to create the project; later runs reuse it.

  Exit code 0 = every check passed, 1 = a check failed or the code did not compile.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools/StickiesSelfTest/run.ps1
#>
param(
    [string]$UnityPath = ""
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

if (-not $UnityPath) {
    $versionLine = Select-String -Path (Join-Path $repo "ProjectSettings\ProjectVersion.txt") -Pattern "m_EditorVersion:\s*(\S+)"
    $version = $versionLine.Matches[0].Groups[1].Value
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}
if (-not (Test-Path $UnityPath)) {
    Write-Error "Unity not found at $UnityPath. Pass -UnityPath <path to Unity.exe>."
    exit 1
}

$work = Join-Path $env:TEMP "StickiesSelfTest"
# Fresh scripts every run; the Library folder is kept so later runs start faster.
if (Test-Path (Join-Path $work "Assets")) { Remove-Item -Recurse -Force (Join-Path $work "Assets") }
foreach ($dir in @("Assets\Stickies", "Assets\Stubs", "Assets\Editor", "Packages")) {
    New-Item -ItemType Directory -Force -Path (Join-Path $work $dir) | Out-Null
}
Copy-Item (Join-Path $repo "Assets\Scripts\Haptics\Stickies\*.cs") (Join-Path $work "Assets\Stickies")
Copy-Item (Join-Path $repo "Assets\Scripts\Editor\StickiesSelfTest.cs") (Join-Path $work "Assets\Editor")
Copy-Item (Join-Path $PSScriptRoot "Stubs.cs") (Join-Path $work "Assets\Stubs")
Copy-Item (Join-Path $PSScriptRoot "manifest.json") (Join-Path $work "Packages\manifest.json")

$log = Join-Path $work "selftest.log"
Write-Host "Running the Stickies self-test in headless Unity ($UnityPath)..."
$arguments = @("-batchmode", "-nographics", "-projectPath", "`"$work`"",
               "-executeMethod", "StickiesSelfTest.RunBatch", "-logFile", "`"$log`"")
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -Wait -PassThru -NoNewWindow

$lines = Select-String -Path $log -Pattern '^\[Stickies\] Self-test:|^(PASS|FAIL)  |^      -> |error CS' |
         ForEach-Object { $_.Line }
if ($lines) { $lines | ForEach-Object { Write-Host $_ } }
else { Write-Host "No self-test output. Full log: $log" }

if ($process.ExitCode -eq 0) { Write-Host "`nAll checks passed." -ForegroundColor Green }
else { Write-Host "`nSelf-test failed (exit code $($process.ExitCode)). Full log: $log" -ForegroundColor Red }
exit $process.ExitCode
