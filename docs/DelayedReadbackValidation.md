# Delayed Readback Validation

Implemented and measured on 2026-09-16 in Release on the NVIDIA GeForce
RTX 5080. This increment reduces CPU observation frequency, not GPU slice
size, pixel-batch size, completion waiting, or journal durability. The user's
reported 80-84% utilization during the preceding 4K DD run is an observation,
not a new telemetry capture or a value calculated from renderer host timers.

## Policy And Ordering

`MANDELBROT_READBACK_SLICES` accepts 1 through 8, default 4. The first slice
is always read back. Thereafter, interval multiples and the final iteration-
budget slice are read back. The counter resets for each pixel batch/pass.
Completed pixels return before altering classifications, resume state, or
arithmetic counters in later slices. Delayed CPU observation can introduce
at most interval-minus-one extra short terminal no-op dispatches per batch.

Each dispatch remains one synchronously completed command list. Explicit
resource-specific UAV barriers for output, resume state, and metrics are now
recorded after the shader in that same list. This makes write ordering explicit
without relying on intermediate copy operations. The public ComputeContext
For/Barrier API is used; context disposal still waits for completion. The
initial build attempt used the internal Run API and failed compilation; it
was corrected to the public For API before the successful full regression run.
No GPU failure or automatic workload retry occurred in this increment.

Microsoft documents UAV barriers as ordering future UAV accesses after prior
accesses to that resource:
[Direct3D 12 resource barriers](https://learn.microsoft.com/en-us/windows/win32/direct3d12/using-resource-barriers-to-synchronize-resource-states-in-direct3d-12).
This is a conservative explicit ordering measure, not evidence that the prior
synchronous path had an established ordering bug or caused the kernel crash.

Journal checkpoints still run after every dispatch, independently of whether
readback was scheduled. The eight-slice journal bound, elapsed checkpoint
target, and per-dispatch one-second brake remain unchanged. Final readback and
EnsureComplete prevent pending classifications reaching repair/coloring.
Direct rendering is unchanged. No asynchronous queueing, multi-dispatch
command-list batching, larger GPU workload, or watchdog setting change was made.

## Regression Results

The full Release suite passed after the ordering changes. Added GPU-free
checks validate default/valid/invalid readback intervals and first/periodic/
final scheduling. Raw FP64/DD fixtures compare intervals 1, 4, and 8 at
identical 128-iteration slice lengths with none/rebase/BLA acceleration.
They require identical escape counts and glitch classifications, unchanged
scalar/rebase/skipped-iteration counters, MPFR agreement for trusted results,
and the bounded extra-dispatch count. They exercised 924 skipped copies.

Additional comparisons cross FP64's 32768-pixel and DD's 8192-pixel seams,
using seven-iteration slices and a nondivisible 257-iteration budget. Sparse
reference/state/staging reuse also runs at interval 8. Timing checks require
each perturbation dispatch to have either one readback or one skipped-readback
count, with consistent per-mode counts and positive transferred-byte totals.
Existing full rendering/repair, 768-bit MPFR, BLA/rebasing, dispatch-limit,
journal failure/equivalence, and complete-journal validation checks passed.

## Profile

One 1920x980 standalone scaling run used the same displayed-coordinate
failure-view fixture, 6912 iterations, 128-iteration slices, eight-slice
journal groups, requested BLA, disabled detailed GPU work counters, and 64
deterministic 768-bit MPFR samples. The scaling mode explicitly selects
readback interval 4. Compare with the preceding grouped-journal profile:

| Measurement | Per-slice readback | Interval 4 |
| --- | ---: | ---: |
| Standalone stage time (s) | 16.802 | 16.928 |
| Renderer time (s) | 16.602 | 16.726 |
| Readback time (s) | 0.463 | 0.160 |
| Slice copies | 5670 | 1575 |
| Copies skipped | 0 | 4095 |
| Dispatch/completion time (s) | 10.916 | 11.024 |
| Journal time (s) | 3.082 | 3.352 |
| Dispatches | 5670 | 5670 |
| Longest dispatch (ms) | 12.608 | 12.630 |

Copy count dropped by 72.22%, and measured readback time by approximately
65.4%. Total time did not improve in this single comparison. Other stage
times varied, and this is not an isolated same-binary repeated benchmark:
the current path also includes explicit UAV barriers. Do not claim an
end-to-end speedup from these results. Dispatch/completion and journaling
remain much larger measured costs than readback at this resolution.

The run transferred 135936420 slice-readback bytes (870 FP64 and 705 DD
copies). It used two references, repaired 2749 pixels, and had zero unresolved
pixels and zero mismatches among 64 MPFR samples. All 5670 submissions and
735 journal groups had matching intents/completions. Windows queries found
no System warning/error/critical events or Application events 1000/1001
since 23:43:39 UTC; query errors were only NoMatchingEventsFound, not access
failures. These checks do not prove all-pixel correctness, driver health,
or resolution of the original 0x119 crash.

At the initial 1920 profile, 2880 and 3804 had not been rerun with this
implementation. The subsequently authorized staged validation is recorded
below. Subsequent GPU-side timing/profiling
should distinguish shader execution from submission/completion overhead
before considering asynchronous or multi-dispatch execution.

## Artifacts

Result: `stage-1920-20260916-234340-46836.jsonl` under
`tests/RendererChecks/bin/Release/net8.0-windows/scaling-results`.
Journal: `dispatch-46836-20260916-234339.jsonl` under the sibling `dispatch-logs`
directory. The raw result stores the absolute journal path, configuration,
and unrounded timings. Generated files remain excluded by the existing
`bin/` ignore rule.

## Higher-Resolution Follow-Up

On 2026-09-16 the full Release regression suite passed again before the
authorized 2880x1460 and 3804x1932 stages. Each ran once in a fresh process.
The intermediate stage's numerical results, complete journal, and Windows
diagnostic queries were reviewed before advancing. No automatic retries,
renderer changes, increased dispatch limits, or settings changes were made.
Both retained interval 4, 128-iteration GPU slices, eight-slice journal groups,
6912 maximum iterations, and the same displayed-coordinate fixture.

| Resolution | Previous total (s) | Interval-4 total (s) | Previous readback (ms) | New readback (ms) | Copies before/after | Longest dispatch (ms) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 2880x1460 | 37.059 | 36.334 | 1109.6 | 333.6 | 12636 / 3510 | 12.925 |
| 3804x1932 | 63.995 | 62.941 | 1798.9 | 571.7 | 22032 / 6120 | 15.069 |

Copy counts decreased 72.22% at each resolution. The single-run total-time
comparisons improved approximately 2.0% and 1.6%, respectively. This is a
small observed improvement, not a repeated or isolated same-binary benchmark;
the newer path also has explicit UAV barriers and other stage times varied.
Do not claim a guaranteed or large end-to-end speedup.

Both used the NVIDIA GeForce RTX 5080, ended in PerturbationDoubleDouble mode,
and used two references. Repaired counts were 6105 and 10570, matching the
previous profiles. Both had zero unresolved pixels and zero mismatches among
64 deterministic 768-bit MPFR samples. Dispatch counts remained 12636 and
22032; no extra terminal no-op submissions were needed in these fixtures.
The grouped-journal validator confirmed preceding intents, bounded authorized
ranges/counts, batch/reference/mode isolation, and matching completion records
for every submission and group (1638 and 2856 groups).

Slice readback counts by FP64/DD were 1935/1575 at 2880 and 3375/2745 at 3804;
skipped copies were 9126 and 15912, and transferred bytes were 303698040 and
530902800. At nearly-4K, dispatch/completion took 43.459 s, journaling 11.905 s,
and MPFR repair 5.906 s. These remain much larger costs than the 0.572 s
readback time. No new GPU-utilization telemetry was captured in this follow-up.

Windows queries found no System warning/error/critical events or Application
events 1000/1001 from 23:47:24 UTC through the final review. Query errors were
only the expected NoMatchingEventsFound result, not access failures. Neither
stage activated the one-second completed-dispatch brake. These successful
single runs do not establish all-pixel correctness, recover undisplayed
original MPFR coordinates, demonstrate repeat-run stability, or prove the
original 0x119 paging-command failure is fixed.

Additional results under `scaling-results`:

- `stage-2880-20260916-234724-8720.jsonl`
- `stage-3804-20260916-234833-44564.jsonl`

Corresponding journals under `dispatch-logs` are
`dispatch-8720-20260916-234724.jsonl` and
`dispatch-44564-20260916-234833.jsonl`. The raw results retain full absolute
paths, configuration, and unrounded measurements.
