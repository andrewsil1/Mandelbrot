param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$StageResult,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$SqlitePath = 'C:/Program Files/NVIDIA Corporation/Nsight Systems 2026.1.3/target-windows-x64/sqlite3.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module "$PSScriptRoot/NsightMeasurements.psm1" -Force
if (!(Test-Path -LiteralPath $Database) -or !(Test-Path -LiteralPath $SqlitePath)) { throw 'Database or sqlite3 is missing.' }
$stage = @(Get-Content -LiteralPath $StageResult | ForEach-Object { $_ | ConvertFrom-Json })
$completed = @($stage | Where-Object phase -eq 'completed')
if ($completed.Count -ne 1 -or !$completed[0].passed -or @($stage | Where-Object phase -eq 'failed').Count -ne 0) {
    throw 'A single passing durable stage result is required.'
}
$result = $completed[0]
$dispatches = @(Get-Content -LiteralPath $result.journal | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object phase -eq 'begin')
# Correlation IDs are process-local: include globalTid, not correlation alone.
$sql = @'
select w.start,w.end,w.commandListType,w.longContextId,a.start apiStart,s.value apiName
from DX12_WORKLOAD w
left join DX12_API a on a.correlationId=w.correlationId and a.globalTid=w.globalTid
left join StringIds s on s.id=a.nameId
order by w.start;
'@
$raw = & $SqlitePath -readonly -json $Database $sql
if ($LASTEXITCODE -ne 0) { throw 'Nsight workload query failed.' }
$work = @(($raw -join "`n") | ConvertFrom-Json)
$measurement = Measure-RendererGpuWork $work $dispatches $result.Timings.DispatchCount
$configured = if ($dispatches[0].PSObject.Properties.Name -contains 'inFlightLimit') { [int]$dispatches[0].inFlightLimit } else { 1 }
if ($configured -notin 1,2 -or $measurement.MaxSubmittedComputeWorkloads -gt $configured) {
    throw 'Captured compute work exceeded the configured submission window.'
}
$raw = & $SqlitePath -readonly -json $Database 'select source,severity,text from DIAGNOSTIC_EVENT order by timestamp;'
if ($LASTEXITCODE -ne 0) { throw 'Nsight diagnostics query failed.' }
$diagnostics = @(($raw -join "`n") | ConvertFrom-Json)
$report = [ordered]@{
    Database = (Resolve-Path -LiteralPath $Database).Path; StageResult = (Resolve-Path -LiteralPath $StageResult).Path
    Adapter = $result.adapter; Passed = $result.passed; Validation = $result.Validation
    RepairedPixels = $result.RepairedCount; UnresolvedPixels = $result.UnresolvedGlitchCount
    RendererMilliseconds = $result.Timings.TotalMilliseconds; HostTimings = $result.Timings
    Gpu = $measurement; Diagnostics = $diagnostics
    Attribution = 'One compute queue, ordered ExecuteCommandLists, exact GPU/journal/renderer counts. Ordinal mode mapping.'
    Limitations = 'Nsight instrumentation enabled; coverage is not Task Manager utilization. CPU sampling disabled. Copy times are not added to compute coverage.'
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$measurement | Format-List
