$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\ProductionHealth.psm1') -Force

foreach ($case in @(
    @{ LogName = 'System'; ProviderName = 'Display'; Id = 4101; Level = 3; Message = ''; expected = $true },
    @{ LogName = 'System'; ProviderName = 'nvlddmkm'; Id = 153; Level = 2; Message = ''; expected = $true },
    @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 41; Level = 1; Message = ''; expected = $true },
    @{ LogName = 'Application'; ProviderName = 'Windows Error Reporting'; Id = 1001; Level = 4; Message = 'LiveKernelEvent 141'; expected = $true },
    @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; Level = 2; Message = 'RendererChecks.exe'; expected = $true },
    @{ LogName = 'Application'; ProviderName = 'Application Error'; Id = 1000; Level = 2; Message = 'Unrelated.exe'; expected = $false },
    @{ LogName = 'System'; ProviderName = 'Display'; Id = 1; Level = 4; Message = 'Informational'; expected = $false }
)) {
    if ((Test-ProductionAdverseEvent ([pscustomobject]$case)) -ne $case.expected) { throw 'Event classifier failed.' }
}
$records = @(
    @{ phase = 'baseline-completed'; Validation = @{ Samples = 64; Mismatches = 0; Unresolved = 0 }; UnresolvedGlitchCount = 0 },
    @{ phase = 'frame-started'; frame = 1; diagnostics = $false },
    @{ phase = 'frame-completed'; frame = 1; exactImage = $true; UnresolvedGlitchCount = 0; productionMs = 100 },
    @{ phase = 'completed'; passed = $true; quietFrames = 1 }
) | ForEach-Object { [pscustomobject]$_ }
if (-not (Test-ProductionProgress $records 1)) { throw 'Passing progress rejected.' }
if ((Test-ProductionProgress $records[0..2] 1) -or (Test-ProductionProgress $records 2)) { throw 'Incomplete run accepted.' }
$records[1].diagnostics = $true
if (Test-ProductionProgress $records 1) { throw 'Instrumented production accepted.' }
$records[1].diagnostics = $false
$records[2].exactImage = $false
if (Test-ProductionProgress $records 1) { throw 'Corrupt image accepted.' }
$records[2].exactImage = $true
if (Test-ProductionProgress ($records + [pscustomobject]@{ phase = 'failed' }) 1) { throw 'Failure accepted.' }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('production-health-' + [guid]::NewGuid() + '.jsonl')
try {
    Write-ProductionRecord $temp @{ phase = 'started' }
    Write-ProductionRecord $temp @{ phase = 'completed'; passed = $true }
    $read = @(Get-Content -LiteralPath $temp | ForEach-Object { $_ | ConvertFrom-Json })
    if ($read.Count -ne 2 -or $read[1].passed -ne $true) { throw 'Durable record roundtrip failed.' }
}
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
Write-Output 'Production health event, progress/failure classification, and durable-record fixtures passed (GPU-free).'
