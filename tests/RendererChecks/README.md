# Renderer checks

Production DD uses explicit FP64 FMA. The normal suite runs a GPU probe of its
actual multiplication, squaring and binary scaling helpers, with a fused-residual
check and 768-bit MPFR arithmetic comparisons. See
[FmaProduction.md](../../docs/FmaProduction.md) for promotion evidence and the
single-frame saved-viewport smoke command. Historical `--fma-experiment` A/B
mode has been removed.

Repair-budget and increasing-resolution viewport workloads are documented in
[RepairBudgetValidation.md](../../docs/RepairBudgetValidation.md). The normal
suite checks the former 1024-pixel boundary, work refunds for early escapes,
hard exhaustion for interior pixels, and lossless viewport coordinate export.
`--interactive-stage WIDTH REPORT.jsonl` runs one standalone workload stage;
run the full matching-configuration suite first.

Run on Windows with a Direct3D 12 adapter supporting double precision:

```powershell
dotnet run --project tests/RendererChecks/RendererChecks.csproj
```

Checks cover unordered sparse pixel mapping, partial-buffer transfers,
non-threadgroup-sized dispatches, buffer reuse, retry merging, preservation of
trusted pixels, and deterministic 768-bit MPFR sample comparisons in all three
render modes. These small checks do not establish correctness for every deep
zoom location or measure 4K performance.

To validate samples of each interactive render, start the application from a
PowerShell session with the following environment variable set:

```powershell
$env:MANDELBROT_VALIDATE = '1'
.\bin\Debug\net8.0-windows\MandelbrotGpu.exe
```

This evaluates an 8-by-8 sample grid using 768-bit orbit arithmetic. Mismatches
appear in the status bar, and unresolved sample counts appear in its tooltip.
It validates the stored viewport, whose construction still uses 384-bit MPFR.
Unset the variable for normal performance. Validation is diagnostic and does
not automatically replace mismatching pixels.

Hover over the status bar for stage timings and the number of perturbation
pixel evaluations. Timings measure host wall-clock time; dispatch and readback
may include driver synchronization, so they are not pure GPU execution times.
Buffers are reused across reference passes within a frame, then disposed.

## Rebasing and BLA validation

Acceleration is enabled by default in both perturbation shaders. The raw-output
fixtures compare all 153 pixels per view against 768-bit MPFR with acceleration
off, rebasing alone, and rebasing plus BLA. They include escaping references,
the -2 tip, a seahorse filament, a period-three component, and deep BLA skips
ending near an escape transition. A trusted mismatching count fails the run;
unresolved pixels are reported explicitly rather than counted as matches.
Counters must prove that both rebases and skips occurred, and skipping must
reduce scalar work relative to rebasing alone without increasing unresolved
pixels. No CPU repair runs in these raw-output comparisons. Each fixture also
runs through the complete rendering/repair pipeline with MPFR sample checks.
Existing sparse/merge/4K dispatch checks also run.

Exact preperiodic boundary fixtures at c=i and scales 1E-28 and 1E-60 cover
pixel-coordinate cancellation and persistent coordinate-error propagation in
both shaders. The default pipeline starts with FP64 perturbation at deep scales;
DD remains available for sparse recovery. Production, queue, and journal checks
explicitly force initial DD where needed to preserve their coverage.

The removed active-pixel compaction and sparse-bound experiments no longer have
runtime settings or a compaction benchmark. BLA profiling and minimum-block
comparisons remain. For the precision comparison methodology, measurements,
incomplete-recovery cases, and command line, see
[DeepZoomOptimizationReview.md](../../docs/DeepZoomOptimizationReview.md).

For interactive comparisons, set the following before launching the app:

```powershell
$env:MANDELBROT_ACCELERATION = 'none'   # ordinary perturbation
$env:MANDELBROT_ACCELERATION = 'rebase' # rebasing, without BLA
$env:MANDELBROT_ACCELERATION = 'bla'    # default: rebasing and BLA
$env:MANDELBROT_METRICS = '1'          # optional detailed work counters
```

Only the final assignment to each variable applies. Restart the application
after changing these settings. Metrics allocate extra per-pixel GPU storage
and perform extra writes/readback; leave them unset for performance comparisons.
The BLA-build stage appears in the tooltip even when counters are disabled.

The BLA tree uses compensated complex coefficients and conservative delta
radii. It keeps all intermediate orbits within the escape disk, disables nodes
near escaping/critical reference steps, and falls back to exact perturbation
steps whenever no block is eligible. A running uncertainty estimate routes
ambiguous escape decisions through the existing glitch/repair path. This is
numerical validation, not a rigorous interval-arithmetic correctness proof.
See https://mathr.co.uk/web/deep-zoom.html for the underlying formulation.

## DirectFloat transition regression

The normal suite includes the reported center near (-0.67323438570448868,
0.35743485497289235), scale 2^-40 and 6,912 iterations. A 256-by-130 check
must trigger DD fallback, retain trusted FP64 pixels, use exactly two reference
passes, and evaluate only the FP64 glitch subset in DD. This catches workload
regressions without depending on adapter-specific timing thresholds.
Fallback is triggered when the FP64 tail exceeds the smaller of 4,096 pixels,
the final repair work allowance, and max(1,024, ceil(0.5% of the frame)). The
final repair allowance is independent of this sparse-DD crossover. The uncertainty check compares against
the escape boundary during iteration; the fixed error cap applies only when
the iteration budget is exhausted. Reference retries stop as soon as the tail
fits the repair budget. Tables with no usable BLA blocks bypass GPU lookup.

For an optional full-resolution workload check, with diagnostic GPU counters
disabled but MPFR sample checks enabled:

```powershell
dotnet run -c Release --project tests/RendererChecks/RendererChecks.csproj -- --transition-4k
```

This standalone check does not replace the normal full regression suite. The
center is reconstructed from the screenshot's displayed doubles, so it cannot
reproduce any undisplayed MPFR coordinate digits from the original viewport.

## GPU device loss and bounded submissions

All render modes submit compact batches with a pixel limit and an
iteration-weighted work limit. Both perturbation shaders additionally resume
their orbits in slices of at most 128 iterations by default. DD uses a smaller
pixel budget because compensated arithmetic is much more expensive. The status
tooltip reports the number of submissions and the longest host-observed
dispatch call. Neither work limit guarantees a runtime on every adapter.

The normal suite includes the device-loss screenshot's displayed center,
checks mapping across direct/FP64 batch boundaries, verifies the DD batch
boundary and final tail, and tests native device-loss error classification.
It does not deliberately reset the GPU or claim to test real driver recovery.

The following optional full-resolution workload previously coincided with a
Windows 0x119 graphics-scheduler bugcheck. It is not part of the normal suite
and must not be used as the next validation step. Slicing is not a proven fix
for that paging-command failure. Increase workloads gradually only after
reviewing small-test diagnostics:

```powershell
dotnet run -c Release --project tests/RendererChecks/RendererChecks.csproj -- --device-loss-4k
```

If Direct3D reports device removal/reset/hang, the renderer discards the frame,
disposes the unusable cached device, and reports the native error code. A
subsequent UI render is blocked until the application is restarted. A completed
submission lasting at least one second also suspends further rendering. This
brake cannot interrupt a pending submission or protect against a kernel crash.

## Resumable perturbation validation

`SliceChecks` runs automatically in every normal regression run. It compares
unsliced raw GPU output with slice lengths 1, 7, 32, 127, 128, 129, and 257 across
small fixtures. Slice lengths are selected per fixture to cover exact/nonexact
budget boundaries without running a large deep-zoom workload. Fixtures include
escaping references, the tip, a period-three component, a filament, BLA escape
transitions, and DD-depth deltas. Every trusted count is checked against 768-bit
MPFR without CPU repair. Without BLA, classifications and accumulated work
counters must match the unsliced path exactly. BLA is capped at slice boundaries
and may choose different approximations; trusted counts must still match MPFR.

DD checks additionally cross the 8192-pixel batch boundary, then reuse the same
workspace with a different reference and 137 reversed sparse indices. The
state allocation must remain batch-sized. Pending classifications must never
escape the GPU pipeline into coloring or repair.

For only the small slice tests:

```powershell
dotnet run -c Debug --project tests/RendererChecks/RendererChecks.csproj -- --slice-only
```

The default slice is 128 iterations. `MANDELBROT_SLICE_ITERATIONS` can select
1 through 32768; 0 explicitly selects unsliced comparison mode and should only
be used for small diagnostic fixtures. Direct FP64 rendering is unchanged.

FP64 saves two delta components, error, pixel iteration, and reference index.
DD saves all four high/low delta components plus the same error and indices.
State slots use batch-local thread indices; classifications and metrics use
global/compact output indices. Initial slices reset state and metrics, resumed
slices skip terminal pixels, and each new batch/reference restarts initialization.
The host reads each batch's classification prefix after every slice and stops
submitting slices when no pixels remain pending. This adds synchronization and
readback overhead; large-frame throughput has not been benchmarked here.
Readback uses one explicit batch-sized staging buffer per FP64 pass or DD
workspace, avoiding the temporary resource allocated by span-based readback.
The DD staging buffer is also reused across sparse reference retries.

## Dispatch journal

Application logs are JSON lines in `%LOCALAPPDATA%\MandelbrotGpu`. Tests write
under their output directory in `dispatch-logs`. `MANDELBROT_LOG_DIRECTORY`
overrides the directory. Each process creates a timestamped file; logs are not
automatically deleted. Every submission flushes a begin record to disk before
GPU work and an end/failure record afterward. An unmatched begin can identify
the last submission attempted before a crash, but does not establish causality.

Begin records include render/submission IDs, adapter name and LUID, advertised
dedicated/shared memory capacities (not current memory usage), reference-pass
number, total iteration budget, acceleration mode, individual buffer byte sizes,
pixel offset/count, and slice start/end. Reported dispatch time excludes journal
I/O and includes fence waiting; intermediate readback time is charged separately.
Disk flushing intentionally adds diagnostic overhead.

See [the pinned submission audit](../../docs/ComputeSharpSubmissionAudit.md)
for completion waiting and native command-resource reuse findings.

## Controlled scaling mode

Run the full matching-configuration regression suite before these standalone
stages. Each invocation renders exactly one stage of the reported device-loss
view, using its displayed center (-0.34562335012588691, 0.625450590999726), scale
2^-40, 6912 iterations, BLA, and 128-iteration slices. Metrics are disabled and
768-bit MPFR validation uses 64 deterministic samples. The center is reconstructed
from doubles and cannot recover undisplayed original MPFR digits.

```powershell
dotnet run -c Release --project tests/RendererChecks/RendererChecks.csproj --no-build -- --scale-stage 256
```

Supported widths map to 256x130, 512x260, 1024x520, 1920x980, 2880x1460,
and 3804x1932. There is no
automatic progression. Review the previous result and dispatch journal before
running the next width. The 3804 stage reproduces the displayed coordinates of
the previous nearly-4K failure case, not its undisplayed MPFR coordinate bits.
The earlier failure rebooted Windows; save work before running larger stages.
Stop on a failed stage, device recovery, or a long-dispatch brake. Do not retry
automatically or advance to the next width after a failure.
Stages run in fresh processes, so reported total times include startup work
inside the measured renderer path rather than representing warmed steady-state
throughput; build/process-launch time is excluded.

Results are flushed JSON-lines files under the test output's `scaling-results`
directory. Records include stage configuration, dispatch-journal location,
adapter identity, total time, precision mode, reference and repair counts,
validation results, and detailed timings. A started-only record is incomplete,
not a pass. Numerical mismatch, unresolved pixels, device loss, and the renderer's
one-second completed-dispatch brake fail the stage without retrying. No later
stage should run after failure. Sample validation is not a proof for all pixels;
successful stages do not establish that the original kernel bugcheck is fixed.

### Host Overhead Profiling

Renderer timing records include total renderer time, durable journal time
(serialization/UTF-8 encoding versus directory/open/write/flush/close), slice
completion scans, MPFR sample validation, and a signed remaining host-time
residual. Journal time is also split by FP64 and DD mode. Inclusive per-mode
slice-loop envelopes cover dispatch, journaling, readback, scans, and shader
construction. Do not add these envelopes or journal subcategories to the
exclusive stage total. Dispatch duration is a synchronous host measurement,
not a GPU timestamp or a measured utilization percentage.

The normal regression suite checks accounting without double-counting, finite
nonnegative stage durations, two durable writes per completed journal group
(or per direct dispatch), complete per-slice record counts,
validation timing coverage, and containment within parent timing envelopes.
Tests assert no hardware-specific performance thresholds. The safety checks
also validate the journal breakdown while checking its durable readback.

For a comparable profile, run one already-passing `--scale-stage 1920` after
the full regression suite. This mode retains the 128-iteration slices, pixel
limits, synchronous completion, one-second brake, and durable journal. It
explicitly selects the four-slice readback interval documented below.
It performs no automatic retry and does not disable logging to boost GPU use.

### Bounded Journal Groups

Perturbation passes default to groups of at most eight consecutive slices of
one batch/reference/mode. `MANDELBROT_JOURNAL_GROUP_SLICES` accepts 1 through
8; there is no logging-disabled setting. The standalone scaling mode explicitly
selects 8. A 100 ms elapsed-time checkpoint target is checked after each
slice's synchronous completion and any scheduled readback. It is not a hard deadline or a
GPU timeout safeguard. GPU workload limits and the one-second brake are unchanged.

Journal schema 2 adds `group-begin`, containing a durable planned slice range,
submission limit, and the first slice's adapter/resource/workload context.
Only after its flush succeeds may GPU work start. Individual `begin`/`end`
records, linked by `journalGroup`, are buffered until a checkpoint; `group-end`
records the actual completed count. Early completion and batch/pass boundaries
flush partial groups. Exceptions, the dispatch brake, and interrupted cleanup
produce `group-failed`, `group-stopped`, or `group-aborted` when logging succeeds.

A kernel crash can leave the entire authorized group uncertain: an intent is
not proof that any slice was submitted, and missing per-slice records are not
proof that no slice completed. Malformed trailing JSON or unmatched groups
must be treated as incomplete, not a pass. Completion evidence is less precise
than durable per-slice logging, but planned workload evidence remains durable
before submission. Normal begin/checkpoint write failures abort; error-path
logging must not mask the original GPU exception or trigger automatic retries.

Regression checks exercise intent-before-submit, the eight-slice bound,
early completion, failure/stop/abort statuses, idempotent disposal, checkpoint
write failure, failed intent authorization, and the elapsed target boundary.
FP64/DD group-size comparisons require identical images,
reference/repair counts, and GPU dispatch counts, with MPFR sample agreement.
Successful run journals are checked for preceding intents, authorized ranges,
batch/reference/mode isolation, and complete matching submissions/groups.

### Delayed Slice Readback

`MANDELBROT_READBACK_SLICES` accepts 1 through 8, default 4; 1 restores
per-slice observation. The first slice is always checked so cheap batches
stop promptly. Subsequently a readback occurs on each interval multiple and
on the final iteration-budget slice, even when that slice is a short tail.
Each batch/pass resets the cadence. A batch that finishes between checks can
receive at most interval-minus-one additional short dispatches. Completed
pixels return before changing their orbit state, classification, or counters.

This does not queue GPU work asynchronously or batch multiple dispatches into
one submission. Explicit UAV barriers for output, resume state, and metrics
are recorded after each shader in its existing single-dispatch command list;
CPU completion waiting and the one-second brake still occur per slice. Journal
checkpoint cadence is independent of readback cadence and remains bounded.
The final readback and pending-state check prevent incomplete CPU results from
reaching histogram coloring or MPFR repair.

Timing records expose total/per-mode slice readback counts, skipped copies,
and transferred bytes. Regression tests compare raw FP64/DD escape counts and
glitch classifications at intervals 1/4/8 with unchanged BLA/rebase/scalar
work counters. They cover first/periodic/final scheduling, invalid settings,
terminal no-op bounds, nondivisible budget tails, both pixel-batch seams,
and sparse reference/staging reuse. Existing 768-bit MPFR and full rendering
pipeline comparisons remain mandatory.
