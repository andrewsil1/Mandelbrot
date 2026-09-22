$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot/../NsightMeasurements.psm1" -Force
$work = @(
    [pscustomobject]@{ start=1000000L; end=3000000L; commandListType=2; longContextId=1; apiStart=10L; apiName='ID3D12CommandQueue::ExecuteCommandLists' },
    [pscustomobject]@{ start=4000000L; end=5000000L; commandListType=2; longContextId=1; apiStart=20L; apiName='ID3D12CommandQueue::ExecuteCommandLists' },
    [pscustomobject]@{ start=1000000L; end=1100000L; commandListType=3 }
)
$dispatch = @([pscustomobject]@{ mode='PerturbationFloat64' }, [pscustomobject]@{ mode='PerturbationDoubleDouble' })
$result = Measure-RendererGpuWork $work $dispatch 2
if ($result.ComputeGpuMilliseconds -ne 3 -or $result.ComputeWindowMilliseconds -ne 4 -or
    $result.ComputeGapMilliseconds -ne 1 -or $result.ComputeWindowCoveragePercent -ne 75 -or $result.CopyGpuMilliseconds -ne 0.1 -or
    $result.Modes[0].GpuMilliseconds -ne 1 -or $result.Modes[1].GpuMilliseconds -ne 2 -or
    $result.MaxSubmittedComputeWorkloads -ne 2) { throw 'GPU time/coverage/mode/unit/window calculation failed.' }
foreach ($case in 'count','overlap','queue','api','inversion','mode') {
    $copy = @($work | ForEach-Object { $_ | Select-Object * })
    $dispatchCopy = @($dispatch | ForEach-Object { $_ | Select-Object * })
    $expected = 2
    switch ($case) {
        count { $expected=3 }
        overlap { $copy[1].start=2000000L }
        queue { $copy[1].longContextId=2 }
        api { $copy[1].apiStart=5L }
        inversion { $copy[0].end=0L }
        mode { $dispatchCopy[1].mode='unknown' }
    }
    $rejected = $false
    try { $null = Measure-RendererGpuWork $copy $dispatchCopy $expected } catch { $rejected=$true }
    if (!$rejected) { throw "Ambiguous/invalid capture accepted: $case" }
}
$third = [pscustomobject]@{ start=6000000L; end=7000000L; commandListType=2; longContextId=1; apiStart=30L; apiName='ID3D12CommandQueue::ExecuteCommandLists' }
$rejected = $false
try { $null = Measure-RendererGpuWork ($work + $third) ($dispatch + $dispatch[0]) 3 } catch { $rejected=$true }
if (!$rejected) { throw 'A three-submission backlog was accepted.' }
Write-Host 'Nsight measurement tests passed: GPU units, queue coverage, mode attribution, rejection of incomplete/ambiguous data.'
