# Uses the already-built Release executable, but stops before device creation.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\ProductionHealth.psm1') -Force
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$executable = Join-Path $root 'tests\RendererChecks\bin\Release\net8.0-windows\RendererChecks.exe'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('production-control-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $directory | Out-Null
$files = @()
try {
    foreach ($frames in @(0, 6)) {
        $errorPath = Join-Path $directory ("invalid-$frames.stderr.txt")
        $outputPath = Join-Path $directory ("invalid-$frames.stdout.txt")
        $files += @($errorPath, $outputPath)
        $process = Start-Process -FilePath $executable -ArgumentList '--production-stage', '256', $frames `
            -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
        if ($process.ExitCode -ne 1 -or (Get-Content -LiteralPath $errorPath -Raw) -notmatch 'ArgumentOutOfRangeException') {
            throw 'Out-of-range repeat count did not stop before rendering.'
        }
        $process.Dispose()
    }
    $stop = Join-Path $directory 'stop.jsonl'
    $progress = Join-Path $directory 'frames.jsonl'
    $errorPath = Join-Path $directory 'stop.stderr.txt'
    $outputPath = Join-Path $directory 'stop.stdout.txt'
    $files += @($stop, $progress, $errorPath, $outputPath)
    Write-ProductionRecord $stop @{ reason = 'Fixture: stop before device creation.' }
    $process = Start-Process -FilePath $executable -ArgumentList '--production-stage', '256', '1',
        ('"' + $progress + '"'), ('"' + $stop + '"') -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
    $records = @(Get-Content -LiteralPath $progress | ForEach-Object { $_ | ConvertFrom-Json })
    if ($process.ExitCode -ne 1 -or @($records | Where-Object phase -eq 'failed').Count -ne 1 -or
        @($records | Where-Object { $_.phase -match 'baseline|frame-' }).Count -ne 0 -or
        (Get-Content -LiteralPath $errorPath -Raw) -notmatch 'External health monitor requested a stop') {
        throw 'Stop request failed to prevent GPU work or preserve failure evidence.'
    }
    $process.Dispose()
}
finally {
    # Only explicitly named fixture files; no recursive deletion.
    foreach ($file in $files) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    Remove-Item -LiteralPath $directory
}
Write-Output 'Production repeat bounds and durable external-stop checks passed before GPU device creation.'
