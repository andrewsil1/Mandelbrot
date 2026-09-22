$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$output = Join-Path $root 'tests\RendererChecks\bin\profiles\parser-check.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
& (Join-Path $root 'tools\AnalyzeRendererTrace.ps1') `
    -CsvPath (Join-Path $PSScriptRoot 'packet-fixture.csv') -RendererPid 42 -OutputPath $output
$result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
$q = $result.queues[0]
if ($result.queues.Count -ne 1 -or $q.CompletedPackets -ne 1 -or $q.PendingPackets -ne 1 `
    -or $q.DuplicateSubmissions -ne 1 -or $q.UnmatchedCompletions -ne 1 `
    -or $q.FailedSubmissions -ne 1 -or $q.EndpointLatencyTotalMs -ne 2 `
    -or $q.MedianMs -ne 2 -or $q.P95Ms -ne 2 -or $q.MaximumMs -ne 2) {
    throw 'Packet parser ownership, pairing, bounds, or microsecond conversion failed.'
}
if ([RendererPacketAnalysis]::Analyze((Join-Path $PSScriptRoot 'packet-fixture.csv'), 99).Length -ne 0) {
    throw 'Packets from another process were attributed to the renderer.'
}
$process = Start-Process -FilePath (Get-Process -Id $PID).Path `
    -ArgumentList '-NoProfile', '-Command', 'exit 0' -WindowStyle Hidden -Wait -PassThru
if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0) {
    throw 'Capture process exit-code collection failed.'
}
Write-Output 'Profiling checks passed: process ownership, CSV quoting, pairing, duplicate/failed/orphan/pending packets, units, and exit-code collection.'
