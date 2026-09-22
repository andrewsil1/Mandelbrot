> Historical experiment report. Active-pixel compaction and pixel-bound BLA have been removed. Their settings and compaction harness are no longer supported. Viewport BLA, its counters, and minimum-block profiling remain.

# Sparse DD BLA Bounds and Profiling

## Scope

This increment investigates actual orbit-iteration savings, not active-dispatch
compaction. Compaction remains disabled and is forced off in the BLA harness.
Numerical tolerances, error propagation, skip acceptance predicates, GPU slice
and pixel limits, readback cadence, and queue/safety limits are unchanged.

An exact global-radius early-rejection gate was tried and removed after Release
timing showed a slowdown. It preserved raw classifications and orbit work, but
reducing tree searches was not sufficient to improve these runs. The final shader
does not include its root parameter, precheck, or cached search-magnitude change.
Its historical measurements are retained separately, not reported as final-build
performance. `MANDELBROT_BLA_GATE` is not a final application setting.

Experimental sparse DD BLA construction bounds the selected pixel centers relative to
the current reference, rather than unused parts of the whole viewport. It scans
the indices for a bounding rectangle, evaluates its axis endpoints with DD
coordinates including low components, and inflates a scaled Euclidean norm by
`1 + 1E-12` and one representable step. The scaled norm avoids underflow from
squaring deep-zoom coordinates. Disconnected regions, reversed order, duplicates,
off-center references, and non-square grids are permitted; invalid indices fail.

Whole-frame DD passes retain the previous viewport-bound fast path. Rebasing-only
passes do not scan sparse indices for BLA bounds. FP64 construction is unchanged.
`MANDELBROT_BLA_BOUND=pixels` opts into the new sparse bound;
unset or `viewport` retains the old bound. The new bound remains disabled by
default because measurements did not show a reliable benefit. Other values are rejected when used.
If a sparse bounding rectangle still spans most of the window, little benefit
is expected. A smaller input bound does not relax the BLA approximation tolerance.
The table still uses approximate error bounds, not interval arithmetic proving
every possible escape count.

## Diagnostics

`MANDELBROT_BLA_PROFILE=1` together with enabled renderer diagnostics and
`MANDELBROT_METRICS=1` records per-DD-pass metadata: selected pixel count, old and
used coordinate bounds, usable internal table nodes, reference length, scalar
iterations, skipped iterations, and rebases. This explicit opt-in is not normal
Release collection, and profiles are not written by the renderer itself.

Detailed GPU BLA counters are compiled into the Debug shader only. They partition
visited multi-step candidates into zero-radius, raw-delta rejection, accumulated-
error rejection, and eligible candidates. They also count search attempts, chosen
blocks, accepted-length histograms (2/4/8/16/32/64/128+), alignment stops, and
terminal reference/budget/slice bounds. Alignment and terminal-bound counters
are search-traversal observations, not a mutually exclusive partition of searches.
Rebasing resets and `r=0` steps are not search attempts.

Each selected pixel owns its counters, with no global atomics. First-slice
initialization and the existing metrics barriers support state resume and buffer
reuse. Debug profiling expands the metric stride from 3 to 21 integers; normal
metrics keep their old layout. Release compiles out the extra shader parameter,
counter operations, and expanded stride. `GpuCountersAvailable=false` explicitly
identifies Release records with no detailed GPU counters. Their zero arrays are
unavailable measurements, not evidence that all candidates were accepted/rejected.

When a table has no usable internal nodes, existing renderer behavior already
downgrades BLA to rebasing-only shader execution. Such a pass has no BLA GPU search
overhead, even when BLA was requested. Its table construction/upload remains.

## Validation and Harness

The mandatory suites include sparse bound checks, configuration parsing, tiny
scaled norms, sampled MPFR containment of pixel deltas, raw 768-bit MPFR escape
counts, alternate references, reversed sparse lists, clustered and dispersed
selection, an 8192-pixel batch seam, and profiling-on/off exact classifications.
They compare old/new bounds with rebasing-only trusted results and require no
increase in glitches for these fixtures. Debug additionally checks candidate
partitioning, accepted-block histograms, and skipped-iteration accounting.
Existing complete pipeline, queue, journal, and quiet production tests remain.

After matching regressions pass, run one explicit experiment at a time:

```powershell
# Debug for detailed rejection/length evidence (not Release performance):
./tests/RendererChecks/bin/Debug/net8.0-windows/RendererChecks.exe --bla-profile 1024 suspended-zoom ./tests/RendererChecks/bin/bla-debug-new.jsonl
./tests/RendererChecks/bin/Debug/net8.0-windows/RendererChecks.exe --bla-sparse-profile 1024 ./tests/RendererChecks/bin/bla-sparse-debug-new.jsonl
# Release for balanced quiet timing comparisons:
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --bla-profile 1024 suspended-zoom ./tests/RendererChecks/bin/bla-release-new.jsonl
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --bla-sparse-profile 1024 ./tests/RendererChecks/bin/bla-sparse-release-new.jsonl
```

Use fresh report filenames: reports append. The harness limits widths to the
existing scaling stages up to 1920, and stops on failure without automatic retries
or changes to watchdog/safety settings. It is not an external Windows health monitor.

The whole-frame mode compares rebasing, BLA with viewport bounds,
and BLA with pixel bounds. Each diagnostic baseline must pass 64 MPFR samples and zero
unresolved pixels, and complete images must agree. Each mode gets one warmup,
then two balanced forward/reverse blocks, four measured quiet frames per mode.
Quiet frames lock the renderer journal against writes and require zero diagnostic
measurements and identical complete images.

The sparse mode forces raw DD passes on a reversed central 63x63 patch of a
larger viewport at scales `1E-18`, `1E-20`, and `1E-28`, with a 4096-iteration
budget. It compares three settings: rebasing, viewport-bound BLA, and pixel-bound
BLA. There is one warmup per setting followed by two
balanced forward/reverse blocks (four measured runs per setting).
Diagnostic trusted raw samples are checked against 768-bit MPFR before
repair can hide errors. Trusted results across modes must agree; glitches are
reported, not repaired. Quiet repeated passes reuse their GPU workspace and
must match their own mode's entire diagnostic classification array exactly.
Their times include reference construction, uploads, table building, and raw DD
execution/readback, but no histogram coloring or MPFR repair. This selected
cluster is a synthetic favorable case, not a promise for dispersed interactive
glitch tails. Debug timings are not used to claim Release speedups.

All timing comparisons are host wall time, not GPU timestamps or utilization.
No new deep 4K performance or driver-health claim is made by this increment.

## Results

The historical transition fixture at 1024x520 had 25,539 sparse DD fallback
pixels, a viewport bound of `4.0174E-12`, and zero usable internal BLA nodes with
either bound. The selected pixels spanned nearly the whole image, so the pixel
bound was `4.0127E-12`. BLA was already downgraded to rebasing-only execution:
there were no GPU tree searches, skipped iterations, or BLA-specific slowdown.

Balanced quiet whole-frame Release means (four measured frames per mode) were:

| Fixture | Rebase | Viewport BLA | Pixel-bound BLA |
| --- | ---: | ---: | ---: |
| suspended-zoom, 1024x520 | 1141.23 ms | 1136.91 ms | 1132.89 ms |

All modes produced the same complete MPFR-validated image, two references, 109
repairs, and zero unresolved pixels. Because all three settings executed the
same rebasing shader work and the spread is below 1%, this is timing variation,
not evidence that pixel bounds improve this view.

The final Release sparse raw-pass means were:

| Scale | Rebase | Viewport BLA | Pixel-bound BLA |
| --- | ---: | ---: | ---: |
| `1E-18` | 30.68 ms | 30.79 ms | 34.01 ms |
| `1E-20` | 30.97 ms | 35.62 ms | 34.79 ms |
| `1E-28` | 30.16 ms | 13.14 ms | 13.18 ms |

At `1E-18`, viewport BLA had no table. Pixel bounds enabled 2,005 usable nodes,
but 12,232,516 search attempts accepted only 7,520 blocks and skipped 15,400 of
12,244,365 scalar iterations (about 0.13%). The tighter bound made the pass about
10.9% slower than rebasing. This is why pixel bounds remain opt-in.

At `1E-20`, viewport BLA skipped 1,777,934 iterations (14.5%) through 489,836
blocks, while pixel bounds skipped 1,790,028 through 461,610 blocks. Both were
slower than rebasing. Their average accepted blocks were only 3.63 and 3.88
iterations: search and DD affine-application cost outweighed scalar work saved.

At `1E-28`, both bounds skipped 11,176,042 of 12,244,365 iterations (91.3%) via
about 893,000 blocks. Their approximately 12.5-iteration mean accepted block was
profitable, cutting the raw pass by about 56%. Pixel bounds changed neither work
nor timing materially at that depth. Existing BLA is therefore valuable, but
the number of skipped iterations alone is not a sufficient profitability test.

No sampled raw result disagreed with 768-bit MPFR, no mode increased glitches,
and every quiet raw classification array matched its diagnostic baseline. The
temporary global-radius gate preserved classifications and scalar/skip/rebase
work but was slower in Release and was removed before the final regressions.

Evidence:

- `tests/RendererChecks/bin/bla-debug-1024-suspended.jsonl`
- `tests/RendererChecks/bin/bla-sparse-debug-1024.jsonl`
- `tests/RendererChecks/bin/bla-release-final-1024-suspended.jsonl`
- `tests/RendererChecks/bin/bla-sparse-release-final-1024.jsonl`

## Minimum profitable block experiment

`MANDELBROT_BLA_MIN_BLOCK` accepts `2`, `4`, `8`, or `16`; the shipping default
is `4`. A rejected BLA candidate continues through the existing exact
double-double recurrence, so increasing this value changes only which safe
approximations are used, not the fallback arithmetic.

The balanced Release harness used a central 63 by 63 sparse patch at 1024 by
520, a 4096-iteration budget, one warmup per mode, and four measured runs in
forward/reverse order. All trusted classifications matched rebasing and sampled
768-bit MPFR results. Mean times in milliseconds were:

| Scale | Rebase | Minimum 2 | Minimum 4 | Minimum 8 | Minimum 16 |
|---|---:|---:|---:|---:|---:|
| `1E-18` | 31.11 | 31.41 | 30.94 | 30.50 | 30.52 |
| `1E-20` | 30.11 | 34.41 | 32.80 | 31.34 | 31.76 |
| `1E-28` | 30.04 | 12.91 | 13.34 | 14.31 | 16.32 |

Minimum 4 is the conservative compromise: relative to minimum 2 it saved 4.7%
at `1E-20` while giving back 3.3% at `1E-28`. Minimum 8 recovered more of the
transition loss but reduced the valuable deep-zoom acceleration by about 11%.

An eager alignment and remaining-range predicate was also tested before every
tree walk. It made minimum 2 slower at both useful depths, so it was removed.
The shader retains the original inexpensive entry condition and applies the
minimum only when considering a node for selection. Evidence is in
`tests/RendererChecks/bin/bla-min-block-no-precheck-release-1024.jsonl`; the
rejected precheck run is `bla-min-block-release-1024.jsonl`.

Final matching Release and Debug suites passed with the minimum block default of
4, the experimental gate removed, and viewport bounds retained as the default.
Each suite validated 18,612 submissions in 5,535 bounded journal groups. The
20-frame fence stress observed handles 701 to 701 in Release and 706 to 708 in
Debug, with exact images and zero unresolved pixels. Existing native-package
NU1701 warnings remain.
