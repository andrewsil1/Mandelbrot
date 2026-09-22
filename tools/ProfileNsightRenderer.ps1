param(
    [ValidateSet('1920','2880','3804')][string]$Width = '1920',
    [ValidateRange(1,2)][int]$InFlight = 2,
    [string]$NsysPath = 'C:/Program Files/NVIDIA Corporation/Nsight Systems 2026.1.3/target-windows-x64/nsys.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe'
if (!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $NsysPath)) { throw 'Validated Release executable or Nsight CLI missing.' }
$directory = Join-Path $root 'tests/RendererChecks/bin/profiles'
$null = New-Item -ItemType Directory -Force -Path $directory
$prefix = Join-Path $directory "nsight-$Width-depth$InFlight-$(Get-Date -Format yyyyMMdd-HHmmss)-$PID"
$previous = $env:MANDELBROT_INFLIGHT
$start = [DateTime]::UtcNow
try {
    $env:MANDELBROT_INFLIGHT = [string]$InFlight
    # Application-scoped DX12 timestamps; no elevated CPU sampling and no
    # watchdog edits, shader enlargement, automatic retries, or stage loops.
    & $NsysPath profile --trace=dx12 --dx12-gpu-workload=individual --dx12-wait-calls=true --sample=none --cpuctxsw=none --export=sqlite --discard-environment=true "--output=$prefix" $exe --scale-stage $Width
    if ($LASTEXITCODE -ne 0) { throw 'Nsight capture failed. Review before any further GPU run.' }
    $stages = @(Get-ChildItem (Join-Path (Split-Path $exe) 'scaling-results') -Filter "stage-$Width-*.jsonl" |
        Where-Object LastWriteTimeUtc -ge $start)
    if ($stages.Count -ne 1) { throw 'Cannot unambiguously identify the captured stage result.' }
    & "$PSScriptRoot/AnalyzeNsightRenderer.ps1" -Database "$prefix.sqlite" -StageResult $stages[0].FullName -OutputPath "$prefix.json" -SqlitePath (Join-Path (Split-Path $NsysPath) 'sqlite3.exe')
    Write-Host "Capture: $prefix.nsys-rep`nAnalysis: $prefix.json"
}
finally { $env:MANDELBROT_INFLIGHT = $previous }
