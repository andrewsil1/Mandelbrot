param([string]$Directory = (Join-Path $PSScriptRoot '../docs/fma-experiment'))
$ErrorActionPreference = 'Stop'
$summary = foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter 'run-*.jsonl' | Sort-Object Name) {
    $records = @(Get-Content -LiteralPath $file.FullName | ConvertFrom-Json)
    if ($records[-1].phase -ne 'passed') { throw "Incomplete experiment: $($file.Name)" }
    $meta = $records[0]
    foreach ($name in 'tip', 'transition') {
        $rows = @($records | Where-Object { $_.phase -eq 'completed' -and $_.name -eq $name })
        if ($rows.Count -ne 10 -or @($rows.hash | Select-Object -Unique).Count -ne 1) { throw "Invalid frame count or image mismatch: $($file.Name)/$name" }
        $medians = @{}
        foreach ($candidate in $false, $true) {
            $times = @($rows | Where-Object { !$_.warmup -and $_.candidate -eq $candidate } | ForEach-Object { $_.milliseconds } | Sort-Object)
            if ($times.Count -ne 4 -or @($times | Where-Object { ![double]::IsFinite($_) -or $_ -le 0 }).Count -ne 0) { throw 'Invalid timing samples' }
            $medians[$candidate.ToString()] = ($times[1] + $times[2]) / 2
        }
        $raw = @($records | Where-Object { $_.phase -eq 'raw' -and $_.name -eq $name })
        if ($raw.Count -ne 1) { throw 'Missing raw comparison' }
        [pscustomobject]@{
            width = $meta.width; height = $meta.height; fixture = $name
            baselineMedianMs = $medians['False']; fmaMedianMs = $medians['True']
            reductionPercent = 100 * (1 - $medians['True'] / $medians['False'])
            rawSelected = $raw[0].selected; rawDifferences = $raw[0].differences
            mpfrChecks = $raw[0].mpfrChecks; repairs = $rows[0].RepairedCount
            assemblySha256 = $meta.assemblySha256; hlslSha256 = $meta.hlslSha256
            report = $file.Name
        }
    }
}
$summary = @($summary | Sort-Object width, fixture)
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Directory 'summary.json')
$summary | Format-Table width, height, fixture, baselineMedianMs, fmaMedianMs, reductionPercent, rawDifferences -AutoSize
