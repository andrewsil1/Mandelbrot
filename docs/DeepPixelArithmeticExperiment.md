# Deep-pixel arithmetic: cost attribution and bounded FP32 experiment

## Decision

Keep the application on its existing FP64-first, sparse-DD, bounded-MPFR
pipeline. The bounded scalar rescaled-FP32 first pass is implemented and tested,
but is available only through internal regression-harness entry points. There
is no application setting or automatic precision-policy change.

The experiment does not improve the slow boundary workload. At 512x260 it
accepts zero transition pixels and changes the balanced median complete-frame
time from 544.08 to 557.67 ms (2.5% slower). It helps a uniform interior fixture,
which does not establish a useful general boundary-rendering improvement.

DD recovery is the largest measured remaining phase of the slow transition
view. Investigating a cheaper recovery representation or reducing DD work has
more potential than promoting this FP32 first pass. These results do not rule
out FP32 with different reference selection, a better justified uncertainty
model, or correctly bounded FP32 BLA; those are different experiments.

## Attribution method

The saved live session supplies the exact stored MPFR viewport terms, including
the zoomed -2 tip and the transition view. See
[frames.jsonl](repair-validation/live-20260920-181453/frames.jsonl). The existing
responsiveness harness reconstructs all stored coordinate terms; only the
aspect ratio changes with resolution. Budgets are 15,714 and 6,912 respectively.

`ProfileTimings` is an internal opt-in that enables host stage timers on the
normal diagnostics-off rendering path. It does not enable journaling, MPFR
sampling, GPU work counters, or change submission/safety policy. Per-mode
recording/submission and completion-wait counters were added. Ordinary
diagnostics-off application rendering still returns zero diagnostic timers.

Each fixture first renders with diagnostics and 64 deterministic 768-bit MPFR
samples. Eight subsequent frames alternate quiet and timed operation in
Q/T/T/Q/Q/T/T/Q order. The first Q/T pair is warmup; three frames of each kind
remain. All eight must exactly match the validated image, have no unresolved
pixels, and preserve reference/fallback/repair counts. Timed frames must have
zero journal writes and non-overlapping exclusive stage accounting. The final
harness additionally holds the existing journal read-locked during those frames;
the recorded attribution runs predate this additional lock assertion.

Runs used the RTX 5080, Release, 128-iteration slices, four-slice readback cadence,
and at most two in-flight submissions. One resolution ran at a time, with review
before advancing. No other experiment ran concurrently on the GPU.

These are **host wall times, not GPU timestamps**. Dispatch includes CPU recording
and submission plus exclusive host time waiting for completion. Submission and
wait are subdivisions of dispatch, not additional costs. Wait does not establish
ALU utilization or distinguish arithmetic, memory traffic, divergence, and queue
residency. Timer-on/off differences and small samples limit precision; in
particular the 1024-wide tip showed about 25 ms difference between their medians.

### Transition medians

| Resolution | Quiet frame ms | Timed frame ms | FP64 dispatch ms | DD dispatch ms |
| --- | ---: | ---: | ---: | ---: |
| 256x130 | 270.25 | 267.63 | 52.20 | 175.43 |
| 1024x520 | 1591.97 | 1596.67 | 559.64 | 873.56 |
| 1920x980 | 5054.34 | 5074.22 | 1924.10 | 2694.68 |
| 3804x1932 | 19547.55 | 19471.34 | 7448.56 | 10401.97 |

At 3804x1932, recording/submission is 785.29 ms and completion waiting is
17,054.98 ms within the dispatch totals. Readback is 421.06 ms, MPFR repair
889.67 ms, reference construction 10.37 ms, BLA construction 0.88 ms, upload
6.17 ms, coloring 65.56 ms, and other host work 214.75 ms. Each number is an
independent median, so they need not sum exactly to the median frame time.

Removing the entire measured FP64 phase at no other cost would reduce the
19.47-second timed frame to roughly 12.02 seconds: about 1.62x. This is an
optimistic phase-removal ceiling, not a predicted FP32 speedup. Replacing
arithmetic cannot remove all submission and fallback costs.

The tip is a different workload: at 3804x1932 its quiet median is 1542.28 ms,
with timed medians of 18.55 ms FP64 dispatch, 611.35 ms DD dispatch, 457.53 ms
MPFR repair, 123.38 ms readback, and 150.18 ms other host work. A faster FP64
first pass alone has little potential there.

## Prototype scope

The GPU kernel evolves `dz = S*w` using `w' = 2*Z*w + S*w*w + d`, with
`dc = S*d`. This follows the rescaling approach described in
[mathr's deep zoom discussion](https://www.mathr.co.uk/blog/2021-05-14_deep_zoom_theory_and_practice.html).
Its reference and viewport are still constructed in MPFR. GPU reference values,
state, recurrence, reconstruction, and uncertainty arithmetic are FP32.

- The harness is limited to 1024x1024 and 32,768 iterations. Measurements stopped
  at 512x260 because the relevant acceptance and timing results were negative.
- Initial scale is a power of two between 1E-30 and 1. Unsupported ranges,
  including the 1E-60 fixture, reject all pixels without dispatching this kernel.
- Normalized delta growth triggers binary rescaling by 4096. Rebasing returns
  to reference index zero at unit scale. Subnormal/flush-to-zero losses have
  conservative error floors; nonfinite or excessive uncertainty rejects pixels.
- Persistent coordinate-construction uncertainty, reference conversion, scalar
  rounding, rebasing and rescaling are accounted for. Escape decisions use
  conservative margins; budget exhaustion retains a 1E-6 trajectory-error gate.
  These remain engineering estimates, not a universal interval proof.
- This first experiment deliberately has **no BLA** in the FP32 pass. The FP64
  recovery pass retains its normal BLA policy. The comparison therefore measures
  this complete candidate implementation, not an isolated FP32/FP64 throughput
  ratio or a fully optimized low-precision renderer.
- Accepted counts seed the FP64 output. Rejected pixels restart at iteration zero
  in FP64; accepted pixels return immediately in the FP64 shader. The fallback
  still dispatches a full grid with early exits, not a compacted sparse FP64 map.
  Subsequent DD and MPFR recovery policies are unchanged. The candidate includes
  another reference build/upload when FP64 recovery is needed.
- The experimental pass reuses the FP64 dispatch safety limits. If invoked with
  diagnostics, its journal distinguishes the kernel by name; its stage counters
  currently share the FP64 bucket. Candidate performance measurements below use
  only diagnostics-off complete-frame wall time, never that combined bucket as
  an isolated FP64 measurement.

## Complete-frame comparison

Both modes use the same binary and existing recovery pipeline. Each gets one
warmup and four measured frames in balanced forward/reverse order. Every frame
must match the baseline pixel array exactly and have zero unresolved pixels.
Conversion, allocations, reference construction, uploads, fallback, repair and
coloring are included. No hardware clock locking or confidence interval is claimed.

| Fixture | FP32 acceptance | 256 baseline/candidate ms | 512 baseline/candidate ms |
| --- | ---: | ---: | ---: |
| Exact live tip | 0% | 38.12 / 40.74 | 52.74 / 46.89 |
| Exact live transition | 0% | 263.40 / 274.16 | 544.08 / 557.67 |
| Exact i boundary, 1E-28 | 0% | 4.30 / 7.11 | 6.47 / 10.81 |
| Rounded filament, 1E-20 | 0% | 22.30 / 29.67 | 57.95 / 68.84 |
| Rounded filament, 1E-28 | 0% | 12.83 / 21.32 | 22.69 / 33.01 |
| Period-three component, 1E-28 | 100% | 10.48 / 4.59 | 23.37 / 8.14 |
| Range bypass, i at 1E-60 | bypass | 5.19 / 4.66 | 7.05 / 8.30 |

All boundary/filament pixels went through the existing recovery pipeline. The
tip's apparent 512-wide improvement cannot be credited to cheaper pixel work:
it accepts no pixels and adds work, with inconsistent direction across sizes.
Likewise, bypass timings measure variability/overhead, not FP32 rendering.
The component gain is reproducible within these samples but is a uniform
interior case. It is insufficient justification for an automatic first pass.

## Correctness and regression evidence

The new checks compare every raw trusted count against 768-bit MPFR on small
escaping, deep-escaping, tip, i-boundary, filament, component, transition and
unsupported-range views. They compare 32- and 128-iteration slices with readback
cadences one and four. A 257x131 escaping fixture checks every trusted count
across multiple batches and a partial final threadgroup. Seeded FP64 rendering
must preserve known counts across those seams. Complete small deep images are
also compared with MPFR-derived colorized images, not just with the old renderer.

The 256/512 profiling runs compare all mutually trusted FP32/FP64 raw counts,
at least 256 deterministic MPFR samples per fixture, and every FP32 acceptance
that FP64 rejected. They then compare complete repaired images in every timed
frame. Large-view samples are not all-pixel MPFR proofs. Deep-escaping was added
to the final regression suite after profiling; it is not a timed benchmark row.

The mandatory full Release renderer regression passed for the initial prototype
and again after the final harness assertions. The final run validated 17,863
submissions in 5,363 bounded journal groups. The twenty-frame quiet-render
handle check went from 694 to 692, with exact images and zero unresolved pixels.
The final application and test-copy hashes match the measured FP32 binary;
see [final-validation.json](arithmetic-experiment/final-validation.json).
Existing NU1701 native-package warnings remain.
No WPF input-to-photon or long-duration driver-stability claim is made.

## Artifacts and reproduction

Raw records and derived medians are under [arithmetic-experiment](arithmetic-experiment/summary.json).
`cost-*.jsonl` and `fp32-*.jsonl` include assembly hashes; the source viewport file
is recorded in each metadata record. Files refuse overwriting. Local root logs
are `cost-attribution-regression.log`, `fp32-regression-release.log`,
`fp32-final-regression-release.log`, `cost-*.log`, and `fp32-profile-*.log`.

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --cost-profile 256 docs/repair-validation/live-20260920-181453/frames.jsonl docs/arithmetic-experiment/cost-new-256.jsonl
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --fp32-profile 256 docs/repair-validation/live-20260920-181453/frames.jsonl docs/arithmetic-experiment/fp32-new-256.jsonl
python tools/SummarizeArithmeticExperiment.py
```

Choose and review each size explicitly. The summarizer verifies completed
records, timing partitions, image hashes and fallback/repair counts before
writing `summary.json`; it never launches GPU work.
