# Deep-zoom responsiveness: sparse retry planning

## Release build

The old windowless Mandelbrot process holding the normal Release executable
was stopped after verifying its executable path and PID. The normal build at
`bin/Release/net8.0-windows/MandelbrotGpu.exe` was rebuilt and passed the full
Release renderer regression suite. The optimized build subsequently passed
the suite again, including the new selector checks. The application and test
copy of `MandelbrotGpu.dll` have matching hashes in
[after-hashes.json](responsiveness/after-hashes.json).

## Baseline before changing the renderer

The unmodified renderer was measured first at 1024x520, 1920x980, and
3804x1932. Its source snapshot and hashes are preserved in
[responsiveness](responsiveness/). No shader, renderer policy, or retry-loop
change was made until all baseline runs completed.

The harness reconstructs the last two exact stored viewports from the live
session: the zoomed -2 tip (15,714 iterations) and transition fixture (6,912).
Only aspect changes for each resolution. Each fixture first runs with
diagnostics and 64-point 768-bit MPFR validation, then runs three times with
diagnostics disabled. Each quiet frame must have zero unresolved pixels,
zero diagnostic timings, no validation object, and the exact same pixel array
as its validated baseline. SHA-256 pixel hashes are compared across renderer
revisions, alongside reference counts, fallback counts, repairs and repair work.

This measures renderer wall time, not WPF input-to-photon latency. Baseline
and optimized processes ran sequentially, not as a randomized benchmark.
First quiet frames showed substantial warm-up/allocation variability; all
samples are retained in the JSON records. Treat these as workload-specific
measurements rather than universal performance guarantees.

## Change

Previously every extra reference rebuilt the unresolved index list from the
entire image, then scanned the entire image twice to choose a glitch tile and
the actual pixel closest to its centroid. The renderer now retains the sorted
unresolved index list across retries and uses it for both selection scans.
A failed reference reuses the list; a successful one filters out resolved
entries. The early return for a tail already within the repair allowance avoids
allocating the list at all.

Tile ordering, centroid arithmetic, nearest-pixel tie breaks, reference-pass
limits, shaders, dispatch limits, GPU queue policy and repair policy are
unchanged. An independent grouping/sorting oracle checks 48 masks, including
empty/dense/sparse masks, tile seams, exclusions and trusted-pixel preservation.

## Measurements

Quiet medians from three renders per fixture and revision:

| Fixture | Resolution | Before | After | Median reduction |
| --- | --- | ---: | ---: | ---: |
| Tip | 1024x520 | 439.3 ms | 290.7 ms | 33.8% |
| Tip | 1920x980 | 566.8 ms | 485.3 ms | 14.4% |
| Tip | 3804x1932 | 1959.1 ms | 1542.3 ms | 21.3% |
| Transition | 1024x520 | 1598.4 ms | 1580.3 ms | 1.1% |
| Transition | 1920x980 | 4976.9 ms | 5019.4 ms | -0.9% |
| Transition | 3804x1932 | 19159.1 ms | 19134.1 ms | 0.1% |

All 36 quiet frames matched their validated baseline pixels exactly. The
before/after hashes, fallback counts, reference counts, repairs and repair work
matched for all six fixture/resolution combinations. The full final Release
regression passed, and the health checker found no matching adverse events.
These are renderer benchmarks; no new native window-input test is claimed.

Final comparison is recorded in [summary.json](responsiveness/summary.json).
The large tip's three quiet baseline times were 6093.2, 1959.1 and 1852.9 ms;
the optimized times were 1637.4, 1542.3 and 1511.9 ms. The median decreased
from 1959.1 to 1542.3 ms, about 21%. Comparing the final two runs separately
also shows an improvement, so the finding does not rely on the 6093 ms first
baseline run. Smaller timings are more sensitive to warm-up and variability.

The transition fixture requires only its initial FP64 and sparse DD passes,
so this retry-planning optimization does not address its main cost. The
unmodified 3804x1932 quiet median was 19.159 seconds, demonstrating that its
long render time is not just diagnostic logging overhead.

## Reproduction

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --responsiveness-profile 1024 docs/repair-validation/live-20260920-181453/frames.jsonl docs/responsiveness/new-1024.jsonl
```

Review the smaller stage before selecting 1920 and 3804. Output files cannot
be overwritten. `tools/SummarizeResponsiveness.py` checks completed
`before-*.jsonl` and `after-*.jsonl` runs and verifies matching output hashes
and recorded rendering work across revisions.

## Next bottleneck

The remaining transition latency warrants investigation of FP64/DD shader
work and submission overhead. Separately, canceling obsolete renders at safe
submission boundaries would reduce the wait after a new zoom/resize request.
Neither GPU-policy changes nor render cancellation is part of this patch.
