# One explicitly selected stage per invocation; review it before advancing.
[CmdletBinding()]
param(
    [ValidateSet('256', '512', '1024', '1920', '2880', '3804')]
    [string] $Stage,
    [ValidateRange(1, 5)]
    [int] $QuietFrames = 1,
    [ValidateSet('original', 'suspended-zoom', 'suspended-zoom-next')]
    [string] $Fixture = 'original',
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ProductionHealth.psm1') -Force
if (-not $Stage) { throw 'Choose one stage explicitly. There is no automatic stage progression.' }
$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $root 'tests\RendererChecks\bin\Release\net8.0-windows\RendererChecks.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build and run the full Release regression suite first.' }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('tests\RendererChecks\bin\production-health\' +
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $Stage + '-' + $PID)
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$health = Join-Path $OutputDirectory 'health.jsonl'
$progress = Join-Path $OutputDirectory 'frames.jsonl'
$stop = Join-Path $OutputDirectory 'stop-requested.txt'
if ((Test-Path -LiteralPath $health) -or (Test-Path -LiteralPath $progress) -or (Test-Path -LiteralPath $stop)) {
    throw 'Output directory contains previous results. Use a fresh directory; do not overwrite failure evidence.'
}
$started = [DateTime]::UtcNow
$exitCode = 1
$process = $null
$adverse = $false
$seen = [Collections.Generic.HashSet[string]]::new()
Write-ProductionRecord $health @{ phase = 'started'; utc = $started.ToString('o'); stage = $Stage;
    quietFrames = $QuietFrames; fixture = $Fixture; executable = $executable; monitorPid = $PID }
try {
    # Necessary static lifetime gate before any device creation. A passing
    # image cannot compensate for a known unsafe native wait lifecycle.
    & (Join-Path $PSScriptRoot 'AuditAsyncFenceLifetime.ps1') -OutputPath (Join-Path $OutputDirectory 'async-fence-audit.json')
    $audit = Get-Content -LiteralPath (Join-Path $OutputDirectory 'async-fence-audit.json') -Raw | ConvertFrom-Json
    if (-not $audit.passedNecessaryCheck) {
        throw 'Native asynchronous fence lifetime audit failed. No GPU workload was launched.'
    }
    # Fail before launch if event logs or dump directories cannot be inspected.
    # DMP contents are never read. Elevation may be needed for their inventory.
    $beforeDumps = @(Get-ProductionDumpInventory)
    $beforeKeys = @($beforeDumps | ForEach-Object { $_.path + '|' + $_.bytes + '|' + $_.lastWriteUtc })
    $null = @(Get-ProductionHealthEvents $started)
    $adapters = @(Get-CimInstance Win32_VideoController | Select-Object Name, PNPDeviceID, DriverVersion, DriverDate)
    $os = Get-CimInstance Win32_OperatingSystem
    Write-ProductionRecord $health @{ phase = 'preflight'; utc = [DateTime]::UtcNow.ToString('o');
        adapters = $adapters; osVersion = $os.Version; osBuild = $os.BuildNumber; lastBoot = $os.LastBootUpTime;
        dumps = $beforeDumps; memorySampling = 'Process working set/private bytes only; no VRAM or GPU utilization measurement.' }
    $process = Start-Process -FilePath $executable -ArgumentList '--production-stage', $Stage, $QuietFrames,
        ('"' + $progress + '"'), ('"' + $stop + '"'), $Fixture -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'renderer.stdout.txt') `
        -RedirectStandardError (Join-Path $OutputDirectory 'renderer.stderr.txt')
    Write-ProductionRecord $health @{ phase = 'child-started'; utc = [DateTime]::UtcNow.ToString('o'); rendererPid = $process.Id }
    while (-not $process.HasExited) {
        $process.Refresh()
        if (-not $process.HasExited) {
            try {
                Write-ProductionRecord $health @{ phase = 'sample'; utc = [DateTime]::UtcNow.ToString('o');
                    workingSetBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64;
                    handles = $process.HandleCount; cpuSeconds = $process.TotalProcessorTime.TotalSeconds }
            }
            catch {
                # Process properties can race a normal exit. An error while it
                # is still alive is an incomplete monitor, not a passing run.
                $process.Refresh()
                if (-not $process.HasExited) {
                    Write-ProductionRecord $health @{ phase = 'monitor-failed'; error = $_.ToString() }
                    $adverse = $true
                }
            }
        }
        try {
            foreach ($event in @(Get-ProductionHealthEvents $started)) {
                if ($seen.Add($event.log + ':' + $event.recordId)) {
                    Write-ProductionRecord $health @{ phase = 'adverse-event'; event = $event }
                    $adverse = $true
                }
            }
            $changedDumps = @(Get-ProductionDumpInventory | Where-Object {
                ($_.path + '|' + $_.bytes + '|' + $_.lastWriteUtc) -notin $beforeKeys
            })
            if ($changedDumps.Count -ne 0) {
                if (-not $adverse) { Write-ProductionRecord $health @{ phase = 'dump-change'; dumps = $changedDumps } }
                $adverse = $true
            }
        }
        catch {
            Write-ProductionRecord $health @{ phase = 'monitor-failed'; error = $_.ToString() }
            $adverse = $true
        }
        if ($adverse -and -not (Test-Path -LiteralPath $stop)) {
            Write-ProductionRecord $stop @{ reason = 'Adverse Windows event or incomplete health monitoring.' }
        }
        # Never reset the GPU or force-kill a workload. The child checks the
        # stop request at frame boundaries; this cannot preempt a hung driver.
        Start-Sleep -Seconds 2
    }
    $process.WaitForExit()
    $childExit = $process.ExitCode
    Write-ProductionRecord $health @{ phase = 'child-exited'; utc = [DateTime]::UtcNow.ToString('o'); rendererExitCode = $childExit }
    # Allow asynchronous WER/event reporting to arrive after process exit.
    Start-Sleep -Seconds 10
    foreach ($event in @(Get-ProductionHealthEvents $started)) {
        if ($seen.Add($event.log + ':' + $event.recordId)) {
            Write-ProductionRecord $health @{ phase = 'adverse-event'; event = $event }
            $adverse = $true
        }
    }
    $afterDumps = @(Get-ProductionDumpInventory)
    $changedDumps = @($afterDumps | Where-Object { ($_.path + '|' + $_.bytes + '|' + $_.lastWriteUtc) -notin $beforeKeys })
    if ($changedDumps.Count -ne 0) { $adverse = $true }
    $records = @(Get-Content -LiteralPath $progress | ForEach-Object { $_ | ConvertFrom-Json })
    $valid = Test-ProductionProgress $records $QuietFrames
    if ($null -eq $childExit -or $childExit -ne 0 -or $adverse -or -not $valid) {
        throw 'Validation or external health checks failed. Stop; do not advance or retry automatically.'
    }
    Write-ProductionRecord $health @{ phase = 'completed'; utc = [DateTime]::UtcNow.ToString('o'); passed = $true;
        rendererExitCode = $childExit; changedDumps = $changedDumps; adverseEventCount = $seen.Count;
        elapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds }
    $exitCode = 0
    Write-Output "PRODUCTION HEALTH PASS: stage=$Stage, quietFrames=$QuietFrames. Evidence: $OutputDirectory"
}
catch {
    if ($null -ne $process -and -not $process.HasExited) {
        Write-ProductionRecord $stop @{ reason = 'External monitor failed. Stop before submitting another frame.' }
        $process.WaitForExit()
    }
    Write-ProductionRecord $health @{ phase = 'failed'; utc = [DateTime]::UtcNow.ToString('o'); passed = $false;
        error = $_.ToString(); changedDumps = $changedDumps }
    Write-Output "PRODUCTION HEALTH FAILED: $($_.Exception.Message) Evidence: $OutputDirectory"
}
finally { if ($null -ne $process) { $process.Dispose() } }
exit $exitCode
