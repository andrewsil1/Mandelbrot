# Repair budget and viewport workload validation

## Policy

Final MPFR repair is capped at 120,000,000 loop evaluations per frame. CPU
workers run in waves: reserve `wavePixels * maxIterations`, join the wave,
then charge actual work and refund early escapes. An escape at iteration i
costs i+1 evaluations (including its final radius check); an interior costs
maxIterations. No wave can overdraw the remaining allowance. This is a work
ceiling, not a latency guarantee. There is no minimum that overrides it.
Pixels are selected in raster order; when the remaining work cannot reserve
one worst-case orbit, the rest remain explicitly unresolved. Trusted GPU
counts are preserved. `FinalRepairLimit` retains its existing API name but now
means the initial guaranteed pixel allowance, not a cap on completed repairs.

Sparse DD fallback retains its previous resolution-sensitive threshold:
`min(finalRepairLimit, 4096, max(1024, ceil(pixelCount * 0.005)))`.
The fallback crossover and final CPU allowance serve different purposes.
Extra references stop when the remaining tail fits the final work allowance.
No shader tolerances, dispatch safety limits, queue depth, or MPFR precision
were relaxed.

The initial policy experiment reused the final allowance for FP64 fallback.
The 256x130 transition workload exposed excessive CPU repair; its evidence is
preserved in `repair-validation/initial-policy`. This policy was corrected
before increasing resolution. The subsequent fixed-pixel policy passed through
1024x520, but its 1920x980 zoomed tip left 18,724 pixels unresolved. That stopped
escalation, and the renderer was changed to refund unused work between waves;
the superseded results are retained in `repair-validation/fixed-pixel-policy`.

## Reproduction and interpretation

Run the full Release regression suite first:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
./tools/ValidateInteractive.ps1 -Width 256
```

Review each stage before selecting 512, 1024, 1920, 2880, or 3804. The script
does not advance automatically and refuses to overwrite an existing report.
Each stage uses a fresh process, then runs 12 sequential frames: transition
and deep -2 tip fixtures, each with base, zoom-in, pan, wider resize, zoom-out,
and resize restoration. The wider target is `width + floor(width / 8)`.
These are renderer-level replays of viewport operations, not injected UI input.
Tip uses 4096 iterations to reproduce the known repair-boundary fixture;
transition uses 6912/7232. These are not a general benchmark of every UI depth.

Each started record preserves the actual 384-bit stored viewport, using exact
binary64 summands encoded as 16-digit hexadecimal IEEE-754 bit patterns. Sum
them in order at 384-bit precision; an empty array is zero. Aspect is a single
binary64 bit pattern. This avoids pretending the rounded status text is exact.
Export fails if a value cannot be represented by this finite expansion.

Completed records contain renderer wall time (including diagnostic logging and
64-point 768-bit MPFR validation), process CPU time, endpoint private bytes and
working set, lifetime peak working set, handles, fallback pixels, reference
passes, repaired/unresolved pixels, and stage timings. Peak working set is
cumulative within the process, not a per-frame allocation peak. The iteration
repair measure `repairIterationUpperBound` is a pessimistic product, whereas
`Timings.RepairIterations` is the charged loop-evaluation count and must stay
within 120,000,000. GPU CSV samples are adapter-wide NVIDIA utilization and used MiB,
include other applications, and cannot be attributed solely to this renderer.
Counters are sampled approximately once per second and can miss short peaks.

Numerical success requires zero unresolved pixels and zero mismatches on the
64-point sample grid. It is not exhaustive correctness proof for large frames.
Failure stops the stage. GPU/application event checks follow each stage.

`MANDELBROT_VIEWPORT_LOG` additionally enables JSONL telemetry in the actual
application. Set it to an existing directory's file path, and enable
`MANDELBROT_DIAGNOSTICS=1` and `MANDELBROT_VALIDATE=1` for stage timings and
sample validation. Ordinary production rendering does not write these records.
Actual UI records measure renderer time; they do not measure input-to-photon
latency, resize debounce, or WPF presentation time.

## Results

The six-stage renderer replay completed all 72 frames with zero unresolved
pixels and zero sample mismatches. Maximum charged repair work was 9,813,480
evaluations, below the 120,000,000 ceiling. Saved measurements and exact
coordinates are in `repair-validation/summary.json` and
`repair-validation/exact-viewports.json`.

The actual WPF window subsequently completed ten instrumented deep-zoom
frames, including rapid zoom input, resizing while rendering, continuous native
sizing, and 3804x1932 transition/tip fixtures. All had zero unresolved pixels
and zero sample mismatches. See [Live window validation](LiveWindowValidation.md)
for timings, resource measurements, exact replay comparison, remaining
performance costs, and validation limits.
