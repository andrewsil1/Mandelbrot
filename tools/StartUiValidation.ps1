param([ValidateSet('transition','tip')][string]$Fixture = 'transition')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root ('docs/repair-validation/live-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $directory | Out-Null
$env:MANDELBROT_UI_FIXTURE = $Fixture
$env:MANDELBROT_VIEWPORT_LOG = Join-Path $directory 'frames.jsonl'
$env:MANDELBROT_DIAGNOSTICS = '1'
$env:MANDELBROT_VALIDATE = '1'
$env:MANDELBROT_METRICS = $null
$env:MANDELBROT_LOG_DIRECTORY = Join-Path $directory 'dispatch'
$exe = Join-Path $root 'bin/LiveValidation/MandelbrotGpu.exe'
$started = [DateTime]::UtcNow
$appProcess = Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Normal -PassThru
[pscustomobject]@{ pid=$appProcess.Id; startedUtc=$started; executable=$exe; fixture=$Fixture;
    sha256=(Get-FileHash (Join-Path (Split-Path $exe) 'MandelbrotGpu.dll')).Hash } |
    ConvertTo-Json | Set-Content (Join-Path $directory 'manifest.json')
Write-Output "UI validation PID $($appProcess.Id), records: $directory"
try {
    while (-not $appProcess.HasExited) {
        $appProcess.Refresh()
        $gpu = & nvidia-smi --query-gpu=timestamp,name,utilization.gpu,memory.used --format=csv,noheader,nounits
        [pscustomobject]@{utc=[DateTime]::UtcNow; pid=$appProcess.Id; cpuMs=$appProcess.TotalProcessorTime.TotalMilliseconds;
            privateBytes=$appProcess.PrivateMemorySize64; workingSet=$appProcess.WorkingSet64; handles=$appProcess.HandleCount;
            adapterWide=$gpu} | ConvertTo-Json -Compress | Add-Content (Join-Path $directory 'resources.jsonl')
        $appProcess.WaitForExit(1000) | Out-Null
    }
    Import-Module (Join-Path $PSScriptRoot 'ProductionHealth.psm1') -Force
    ConvertTo-Json -InputObject @(Get-ProductionHealthEvents -SinceUtc $started) -Depth 8 |
        Set-Content (Join-Path $directory 'health.json')
} finally { $appProcess.Dispose() }
