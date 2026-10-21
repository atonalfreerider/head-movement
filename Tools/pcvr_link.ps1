<#
Starts the Windows (PC VR) build over Meta Quest Link / Air Link with the splat dancers on, and summarises its log after it closes.
docs/QUEST.md section 6, docs/SPLAT_DANCERS.md "PC VR over Link".

    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1                         # the library menu in the headset, splats on
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -Play recap -Seek 23     # straight into the class recap at film 23 s
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -SplatSet quest_40k       # the distilled 2 x 40k set (recap; fill ~1/10 of the full set)
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -SplatSigma 0.01          # every Gaussian's sigma clamped to 1 cm (less fill, looks as full)
    # -SplatScale S (<1: SplatLowResFeature, the Quest's 1x-MSAA splat layer) is passed through but UNTESTED on D3D12: switching it on in
    # the Editor on D3D12 crashed the Editor (2026-10-21 03:57) - not for the first Link test
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -Extra "-hmsplatcutoff 0.02"   # any other switch (docs/QUEST.md, VrSettings.cs)
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -Splats off              # the mesh avatars (A/B)
    powershell -ExecutionPolicy Bypass -File Tools/pcvr_link.ps1 -Summary <log>           # only summarise a log of an earlier run

What it does (it changes no setting of the PC, the headset or Link):
  - checks that Meta Horizon Link is the active OpenXR runtime (registry, read only) and that OVRService runs;
  - sets HM_DATA to Builds/data (the lessons laid out by Tools/deploy_data.ps1 -Out Builds/data) when that folder exists;
  - starts Builds/vr/HeadMovement.exe with -force-d3d12 (the splat renderer needs D3D12; this 4-thread PC would pick D3D11),
    -hmvr on (VR at once), -hmvr-display immersive (passthrough is a Quest-standalone feature), -hmsplats on, and the extra switches;
  - writes the Player log to Builds/vr/logs/pcvr_<time>.log and, when the app closes, prints the HM-SPLATS / HM-PERF / VrMode lines.
#>
param(
    [ValidateSet("on", "off")][string]$Splats = "on",
    [int]$SplatMax = 0,
    [double]$SplatSigma = 0,
    [double]$SplatScale = 0,
    [string]$SplatSet = "",
    [ValidateSet("", "composite", "direct")][string]$SplatPath = "",
    [ValidateSet("", "full", "light", "off")][string]$Hair = "",
    [string]$Play = "",
    [double]$Seek = -1,
    [ValidateSet("", "start", "front", "side", "overhead")][string]$View = "",
    [string]$Exe = "",
    [string]$Data = "",
    [string]$Summary = "",
    [string]$Extra = "",
    [switch]$NoWait
)

$ErrorActionPreference = "Stop"
$project = Split-Path $PSScriptRoot -Parent

function Show-Summary([string]$log) {
    if (-not (Test-Path $log)) { Write-Host "no log at $log"; return }
    Write-Host "`n== $log"
    $lines = Get-Content $log -Encoding UTF8
    $keep = $lines | Where-Object { ($_ -match "^(VrMode:|VrMenu:|VrDisplay:|VrPlacement:|HM-SPLATS|HM-PERF|HM-LOAD done|HM-STALL|Forcing GfxDevice|    Version: +Direct3D)" -or $_ -cmatch "XR_ERROR|Exception: ") -and $_ -notmatch "^\[XR\].*(Info|Report Reason)" }
    $keep | Select-Object -First 60 | ForEach-Object { Write-Host $_ }
    if ($keep.Count -gt 60) { Write-Host "... ($($keep.Count - 60) more matching lines; last ones:)"; $keep | Select-Object -Last 15 | ForEach-Object { Write-Host $_ } }
    $perf = $lines | Where-Object { $_ -match "HM-PERF" -and $_ -match "fps" }
    if ($perf) { Write-Host "`nframe statistics (HM-PERF, every 5 s): $($perf.Count) lines - the last one:"; Write-Host ($perf | Select-Object -Last 1) }
}

if ($Summary) { Show-Summary $Summary; return }

$exePath = if ($Exe) { $Exe } else { Join-Path $project "Builds/vr/HeadMovement.exe" }
if (-not (Test-Path $exePath)) { throw "no build at $exePath (unity command hm_vr_build --target windows --output Builds/vr --development false)" }

# the OpenXR runtime (read only)
$rt = $null
try { $rt = (Get-ItemProperty "HKLM:\SOFTWARE\Khronos\OpenXR\1" -ErrorAction Stop).ActiveRuntime } catch {}
if (-not $rt) { Write-Warning "no active OpenXR runtime: in Meta Horizon Link, Settings > General > set Meta Horizon Link as the active OpenXR runtime" }
elseif ($rt -notmatch "oculus_openxr") { Write-Warning "the active OpenXR runtime is $rt (not Meta Horizon Link): the app talks to that runtime" }
else { Write-Host "OpenXR runtime: $rt" }
$svc = Get-Service -Name "OVRService" -ErrorAction SilentlyContinue
if (-not $svc -or $svc.Status -ne "Running") { Write-Warning "OVRService is not running: start Meta Horizon Link first" }

# the data
$dataDir = if ($Data) { $Data } else { Join-Path $project "Builds/data" }
if (Test-Path (Join-Path $dataDir "library/library.json")) { $env:HM_DATA = (Resolve-Path $dataDir).Path; Write-Host "HM_DATA = $env:HM_DATA" }
else { Write-Warning "no laid-out data at $dataDir (powershell -File Tools/deploy_data.ps1 -Out Builds/data): the build reads its own StreamingAssets" }

$logDir = Join-Path (Split-Path $exePath -Parent) "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("pcvr_{0}.log" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
$a = @("-force-d3d12", "-hmvr", "on", "-hmvr-display", "immersive", "-hmsplats", $Splats, "-logFile", $log)
if ($SplatMax -ne 0) { $a += @("-hmsplatmax", "$SplatMax") }
if ($SplatSigma -ne 0) { $a += @("-hmsplatsigma", $SplatSigma.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($SplatSet) { $a += @("-hmsplatset", $SplatSet) }
if ($SplatScale -ne 0) { $a += @("-hmsplatscale", $SplatScale.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($SplatPath) { $a += @("-hmsplatpath", $SplatPath) }
if ($Hair) { $a += @("-hmhair", $Hair) }
if ($Play) { $a += @("-hmplay", $Play) }
if ($Seek -ge 0) { $a += @("-hmseek", $Seek.ToString([Globalization.CultureInfo]::InvariantCulture)) }
if ($View) { $a += @("-hmview", $View) }
if ($Extra) { $a += @($Extra -split "\s+" | Where-Object { $_ }) }
Write-Host "starting $exePath $($a -join ' ')"
$p = Start-Process -FilePath $exePath -ArgumentList $a -PassThru
if ($NoWait) { Write-Host "running (pid $($p.Id)); summary later: powershell -File Tools/pcvr_link.ps1 -Summary `"$log`""; return }
Write-Host "running (pid $($p.Id)); close the app (menu Exit VR, then close the window) to see the summary"
$p.WaitForExit()
Show-Summary $log
