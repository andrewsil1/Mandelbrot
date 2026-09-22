Set-StrictMode -Version Latest

function Measure-RendererGpuWork {
    param([object[]]$Workloads, [object[]]$Dispatches, [int]$ExpectedDispatches)
    $compute = @($Workloads | Where-Object { $_.commandListType -eq 2 } | Sort-Object { [long]$_.start })
    if ($compute.Count -eq 0 -or $compute.Count -ne $ExpectedDispatches -or $Dispatches.Count -ne $compute.Count) {
        throw 'Compute workload, renderer dispatch, and journal counts must agree and be nonzero.'
    }
    if (@($compute | Select-Object -ExpandProperty longContextId -Unique).Count -ne 1) {
        throw 'Mode attribution requires exactly one captured compute queue.'
    }
    $totals = @{}
    $events = [System.Collections.Generic.List[object]]::new()
    [long]$previousEnd = -1
    [long]$previousApi = -1
    for ($i = 0; $i -lt $compute.Count; $i++) {
        $work = $compute[$i]
        [long]$start = $work.start; [long]$end = $work.end; [long]$api = $work.apiStart
        # One ExecuteCommandLists per journal dispatch on one ordered compute
        # queue permits ordinal mode attribution. Reject ambiguity, not guess.
        if ($start -lt 0 -or $end -le $start -or $start -lt $previousEnd -or $api -le $previousApi -or
            $api -gt $start -or $work.apiName -ne 'ID3D12CommandQueue::ExecuteCommandLists') {
            throw 'Invalid, overlapping, unordered, or unattributed compute workload timestamps.'
        }
        $previousEnd = $end; $previousApi = $api
        $events.Add([pscustomobject]@{ Time = $api; Delta = 1 })
        $events.Add([pscustomobject]@{ Time = $end; Delta = -1 })
        $mode = [string]$Dispatches[$i].mode
        if ($mode -notin 'PerturbationFloat64','PerturbationDoubleDouble') { throw 'Unsupported journal mode.' }
        if (!$totals.ContainsKey($mode)) { $totals[$mode] = @{ Count = 0; Nanoseconds = 0L; Maximum = 0L } }
        $totals[$mode].Count++
        $totals[$mode].Nanoseconds += $end - $start
        $totals[$mode].Maximum = [Math]::Max($totals[$mode].Maximum, $end - $start)
    }
    $modes = @($totals.Keys | Sort-Object | ForEach-Object {
        [pscustomobject]@{ Mode = $_; Dispatches = $totals[$_].Count;
            GpuMilliseconds = $totals[$_].Nanoseconds / 1e6; MaxGpuMilliseconds = $totals[$_].Maximum / 1e6 }
    })
    $gpuMs = ($modes | Measure-Object -Property GpuMilliseconds -Sum).Sum
    $outstanding = 0; $peak = 0
    foreach ($event in ($events | Sort-Object Time,Delta)) {
        $outstanding += $event.Delta
        $peak = [Math]::Max($peak, $outstanding)
    }
    if ($outstanding -ne 0 -or $peak -gt 2) { throw 'Captured outstanding compute work exceeded the bounded window.' }
    $windowMs = ([long]$compute[-1].end - [long]$compute[0].start) / 1e6
    $copies = @($Workloads | Where-Object { $_.commandListType -eq 3 })
    [long]$copyNs = 0
    foreach ($copy in $copies) {
        if ([long]$copy.start -lt 0 -or [long]$copy.end -le [long]$copy.start) { throw 'Invalid copy timestamp.' }
        $copyNs += [long]$copy.end - [long]$copy.start
    }
    [pscustomobject]@{
        ComputeDispatches = $compute.Count; ComputeGpuMilliseconds = $gpuMs; Modes = $modes
        MaxSubmittedComputeWorkloads = $peak
        ComputeWindowMilliseconds = $windowMs; ComputeGapMilliseconds = $windowMs - $gpuMs
        # Coverage describes this queue's captured interval, not Task Manager
        # utilization, occupancy, or aggregate utilization across other engines.
        ComputeWindowCoveragePercent = 100 * $gpuMs / $windowMs
        CopyWorkloads = $copies.Count; CopyGpuMilliseconds = $copyNs / 1e6
    }
}
Export-ModuleMember -Function Measure-RendererGpuWork
