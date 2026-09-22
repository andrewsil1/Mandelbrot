$ErrorActionPreference = 'Stop'

function Write-ProductionRecord {
    param([string] $Path, [object] $Record)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Record | ConvertTo-Json -Depth 12 -Compress) + "`n")
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Append, [IO.FileAccess]::Write,
        [IO.FileShare]::Read, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Test-ProductionAdverseEvent {
    param([object] $Event)
    # WER reports can be informational. Match GPU/kernel reports and this
    # executable, not every unrelated application error on the workstation.
    if ($Event.LogName -eq 'System') {
        return (($Event.Level -le 3 -and $Event.ProviderName -match 'Display|nvlddmkm|igfx|igdkmd|DxgKrnl|WER-SystemErrorReporting') -or
            ($Event.ProviderName -eq 'Microsoft-Windows-Kernel-Power' -and $Event.Id -eq 41))
    }
    return ($Event.ProviderName -match 'Application Error|Windows Error Reporting' -and
        $Event.Message -match 'RendererChecks|MandelbrotGpu|LiveKernelEvent|BlueScreen')
}

function Get-ProductionHealthEvents {
    param([datetime] $SinceUtc)
    foreach ($log in @('System', 'Application')) {
        try {
            $events = @(Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $SinceUtc.ToLocalTime() } -ErrorAction Stop)
        }
        catch {
            if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { continue }
            throw
        }
        foreach ($event in $events) {
            if ($event.TimeCreated.ToUniversalTime() -le $SinceUtc) { continue }
            if (Test-ProductionAdverseEvent $event) {
                [pscustomobject]@{ log = $log; recordId = $event.RecordId; utc = $event.TimeCreated.ToUniversalTime().ToString('o');
                    provider = $event.ProviderName; id = $event.Id; level = $event.Level; message = $event.Message }
            }
        }
    }
}

function Get-ProductionDumpInventory {
    foreach ($directory in @((Join-Path $env:SystemRoot 'LiveKernelReports'), (Join-Path $env:SystemRoot 'Minidump'))) {
        if (Test-Path -LiteralPath $directory) {
            Get-ChildItem -LiteralPath $directory -Filter '*.dmp' -File -Recurse -ErrorAction Stop |
                ForEach-Object { [pscustomobject]@{ path = $_.FullName; bytes = $_.Length; lastWriteUtc = $_.LastWriteTimeUtc.ToString('o') } }
        }
    }
    $kernelDump = Join-Path $env:SystemRoot 'MEMORY.DMP'
    if (Test-Path -LiteralPath $kernelDump) {
        $item = Get-Item -LiteralPath $kernelDump -ErrorAction Stop
        [pscustomobject]@{ path = $item.FullName; bytes = $item.Length; lastWriteUtc = $item.LastWriteTimeUtc.ToString('o') }
    }
}

function Test-ProductionProgress {
    param([object[]] $Records, [int] $Frames)
    if (@($Records | Where-Object phase -eq 'failed').Count -ne 0) { return $false }
    $complete = @($Records | Where-Object phase -eq 'completed')
    $baseline = @($Records | Where-Object phase -eq 'baseline-completed')
    $starts = @($Records | Where-Object phase -eq 'frame-started')
    $ends = @($Records | Where-Object phase -eq 'frame-completed')
    if ($complete.Count -ne 1 -or $complete[0].passed -ne $true -or $complete[0].quietFrames -ne $Frames -or
        $baseline.Count -ne 1 -or $baseline[0].Validation.Samples -ne 64 -or
        $baseline[0].Validation.Mismatches -ne 0 -or $baseline[0].Validation.Unresolved -ne 0 -or
        $baseline[0].UnresolvedGlitchCount -ne 0 -or $starts.Count -ne $Frames -or $ends.Count -ne $Frames) { return $false }
    for ($i = 0; $i -lt $Frames; $i++) {
        if ($starts[$i].frame -ne ($i + 1) -or $starts[$i].diagnostics -ne $false -or
            $ends[$i].frame -ne ($i + 1) -or $ends[$i].exactImage -ne $true -or
            $ends[$i].UnresolvedGlitchCount -ne 0 -or $ends[$i].productionMs -le 0) { return $false }
    }
    return $true
}

Export-ModuleMember -Function Write-ProductionRecord, Test-ProductionAdverseEvent, Get-ProductionHealthEvents,
    Get-ProductionDumpInventory, Test-ProductionProgress
