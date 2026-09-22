# Run from an elevated PowerShell session. Uses the already-built Release
# executable: this capture is not a substitute for the full regression suite.
[CmdletBinding()]
param(
    [ValidateSet('1920')]
    [string] $Stage = '1920',
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $root 'tests\RendererChecks\bin\Release\net8.0-windows\RendererChecks.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build and run the full Release regressions first.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'GPU/CPU WPR recording requires an elevated PowerShell session.'
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('tests\RendererChecks\bin\profiles\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$log = Join-Path $OutputDirectory 'capture.log'
$trace = Join-Path $OutputDirectory 'renderer.etl'
$metadata = Join-Path $OutputDirectory 'capture.json'
$record = [ordered]@{ startedUtc = [DateTime]::UtcNow.ToString('o'); stage = $Stage;
    executable = $executable; trace = $trace; elevated = $true; rendererExitCode = $null;
    stopExitCode = $null; completedUtc = $null; error = $null }
$ownRecording = $false
$exitCode = 1
try {
    # Refuse to cancel or append to another user's recording. The installed
    # English WPR reports this exact idle status; unknown output is fail-closed.
    $status = & wpr.exe -status 2>&1
    $status | Out-File -LiteralPath $log -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or ($status -join "`n") -notmatch 'WPR is not recording') {
        throw 'WPR is active or its idle status could not be confirmed.'
    }
    & wpr.exe -start GPU -start CPU -filemode 2>&1 | Out-File -LiteralPath $log -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "WPR start failed: $LASTEXITCODE" }
    $ownRecording = $true
    # One fresh process; no automatic retry, progression, or dispatch-policy change.
    $process = Start-Process -FilePath $executable -ArgumentList '--scale-stage', $Stage `
        -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'renderer.stdout.txt') `
        -RedirectStandardError (Join-Path $OutputDirectory 'renderer.stderr.txt')
    $record.rendererPid = $process.Id
    $record.rendererExitCode = $process.ExitCode
    if ($null -eq $process.ExitCode) { throw 'Renderer exit status is unavailable; inspect the durable scaling result.' }
    if ($process.ExitCode -ne 0) { throw "Renderer validation failed: $($process.ExitCode). Do not retry automatically." }
    $exitCode = 0
}
catch {
    $record.error = $_.ToString()
    $_ | Out-File -LiteralPath $log -Append -Encoding utf8
}
finally {
    if ($ownRecording) {
        & wpr.exe -stop $trace 2>&1 | Out-File -LiteralPath $log -Append -Encoding utf8
        $record.stopExitCode = $LASTEXITCODE
        if ($LASTEXITCODE -ne 0) { $exitCode = 1 }
    }
    $record.completedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json | Out-File -LiteralPath $metadata -Encoding utf8
}
exit $exitCode
