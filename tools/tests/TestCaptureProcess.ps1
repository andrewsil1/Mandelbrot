# Exercise the capture wrapper's process-exit mechanism under Windows
# PowerShell 5.1 as well as PowerShell 7, without running any GPU workload.
$ErrorActionPreference = 'Stop'
foreach ($expected in @(0, 37)) {
    $process = Start-Process -FilePath (Get-Process -Id $PID).Path `
        -ArgumentList '-NoProfile', '-Command', ('exit ' + $expected) `
        -WindowStyle Hidden -Wait -PassThru
    if ($null -eq $process.ExitCode -or $process.ExitCode -ne $expected) {
        throw "Expected exit code $expected; got $($process.ExitCode)."
    }
}
Write-Output 'Capture exit status checks passed for successful and failed child processes.'
