param([Parameter(Mandatory)][ValidateSet(256,512,1024,1920,2880,3804)][int]$Width)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$resultDirectory = Join-Path $root 'docs/repair-validation'
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$prefix = Join-Path $resultDirectory "stage-$Width"
if (Test-Path "$prefix.jsonl") { throw "Existing results at $prefix.jsonl; archive them before another run." }
$exe = Join-Path $root 'tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $exe -ArgumentList @('--interactive-stage', $Width, ('"' + $prefix + '.jsonl"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$prefix.stdout.txt" -RedirectStandardError "$prefix.stderr.txt"
try {
    while (-not $process.HasExited) {
        # Adapter-wide readings include other applications; not per-process GPU attribution.
        $gpu = & nvidia-smi --query-gpu=timestamp,name,utilization.gpu,memory.used --format=csv,noheader,nounits
        [pscustomobject]@{ utc = [DateTime]::UtcNow.ToString('o'); pid = $process.Id; adapterWide = $gpu } |
            ConvertTo-Json -Compress | Add-Content "$prefix.gpu.jsonl"
        $process.WaitForExit(1000) | Out-Null
        $process.Refresh()
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Stage failed, exit $($process.ExitCode). Stop escalation; inspect $prefix.stderr.txt" }
    Import-Module (Join-Path $PSScriptRoot 'ProductionHealth.psm1') -Force
    $events = @(Get-ProductionHealthEvents -SinceUtc $started)
    ConvertTo-Json -InputObject $events -Depth 8 | Set-Content "$prefix.health.json"
    if ($events.Count) { throw 'Adverse GPU/application event detected. Stop escalation.' }
    Get-Content "$prefix.stdout.txt" | Select-Object -Last 12
}
finally { $process.Dispose() }
