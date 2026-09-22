# Host Overhead Profile

This records the per-slice durable-logging baseline. The subsequently
implemented bounded grouping and its measured result are documented in
[GroupedJournalValidation.md](GroupedJournalValidation.md).

Measured on 2026-09-16, Release, NVIDIA GeForce RTX 5080. The full Release
regression suite passed before one instrumented 1920x980 scaling run. No
automatic retries, logging disablement, larger slices, or increased pixel
limits were used. This is measurement, not a throughput optimization.

## Instrumentation

`RenderTimings` records exclusive stage durations, journal serialization and
file-operation breakdowns, per-mode journal totals, slice completion scans,
sample validation, and total renderer time. Inclusive FP64/DD slice-loop
envelopes allow comparison of dispatch time with the surrounding host work.
These envelopes and journal breakdowns are not summed into the exclusive
accounting total. The remaining host time is signed so overlapping timers
would remain visible.

`DispatchJournal.Write` still opens/appends, uses write-through, flushes to
disk, and closes each record. Every successful dispatch retains its durable
begin and end records. File timing covers directory setup, open, write,
flush, and close; it is not a separate measurement of the flush alone.
Serialization timing covers JSON serialization and UTF-8 encoding. Total
journal time also includes lock acquisition and timing overhead.

The renderer total begins at `Render`, ends after frame resources are released,
and excludes device acquisition in the renderer constructor. The standalone
stage total additionally includes its device/viewport setup. Neither number
includes process launch/build time.

## Results

| Measurement | Seconds |
| --- | ---: |
| Standalone stage total | 36.044 |
| Renderer total | 35.843 |
| Journal total | 21.843 |
| Journal file operations (included above) | 21.731 |
| Journal serialization (included above) | 0.110 |
| Dispatch plus synchronous completion | 11.224 |
| Readback | 0.628 |
| MPFR repair | 1.560 |
| MPFR sample validation | 0.419 |
| Slice completion scans | 0.002 |
| Other host residual | 0.068 |

Journal writes consumed 60.94% of total renderer time. There were 11340 writes
for 5670 dispatches, averaging 1.926 ms per write. Reference, upload, BLA, and
coloring account for the remaining small measured stages. This identifies
journaling as the largest measured cost in this run, without requiring a
logging-disabled comparison.

| Phase | Inclusive slice loop (s) | Dispatch/completion (s) | Journal (s) | Dispatch share of loop |
| --- | ---: | ---: | ---: | ---: |
| FP64 | 15.306 | 2.879 | 12.048 | 18.81% |
| Double-double | 18.433 | 8.346 | 9.795 | 45.27% |

These shares are host wall-clock ratios, not GPU timestamps, occupancy, or
measured utilization percentages. They are nevertheless consistent with
the user's lower FP64 and higher DD utilization observations: journal writes
are CPU/file work performed between synchronous GPU submissions, while the
DD compute portion occupies more of its surrounding loop time.

The run ended in PerturbationDoubleDouble mode with two references, 2749
repaired pixels, zero unresolved pixels, and zero mismatches among 64
deterministic 768-bit MPFR samples. The journal had 5670 matching begin/end
records, unique completion IDs, no failed records or unmatched begins, and
maximum slice length 128. Longest dispatch was 12.2524 ms. Post-run queries
found no System warning/error/critical events or Application events 1000/1001
since stage start. These checks do not establish all-pixel correctness or
prove that the previous kernel failure is fixed.

## Utilization Interpretation

There is no percentage-based GPU utilization limit or deliberate sleep in the
renderer. There are explicit conservative work limits: 128 logical iterations
per slice by default, up to 32768 FP64 pixels or 8192 DD pixels per dispatch,
and synchronous dispatch followed by readback and a pending-pixel scan before
the next slice. The one-second brake stops future submissions only after an
overlong call completes; it does not regulate usage to a target percentage.

This serialized scheduling leaves the GPU without this application's next
compute submission while the CPU logs, reads back, scans, and constructs it.
Small batches can also affect device occupancy; this profile does not measure
that contribution. DD arithmetic, divergence, reference access, and active
pixel distribution remain potential device-side costs.

Microsoft describes Task Manager's aggregate utilization as that of the
busiest engine, not a fraction of every GPU arithmetic unit:
[GPUs in Task Manager](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/).
For an actual queue timeline and engine activity, use a
[PIX timing capture](https://learn.microsoft.com/en-us/windows/win32/direct3dtools/pix/articles/timing-captures/pix-timing-captures).
Do not infer that the former 100% readings caused the crash or that current
lower readings prove safety. The prior failure involved paging-command
submission, and its root cause remains unestablished.

## Next Performance Increment

Address journal overhead before enlarging GPU workloads. Retaining an open
stream can avoid repeated open/close work while preserving durable flushes,
but this profile does not identify how much that alone would save. A larger
potential improvement is amortizing durable journal/checkpoint writes over
small, explicitly bounded groups of short slices. That requires a documented
crash-evidence policy: durable pre-submission records must describe the group,
and a crash may leave a group of ambiguous completion records rather than one
precise unmatched slice. Do not silently weaken that policy or flush only at
frame end. Keep dispatch work limits and failure-stop behavior intact.

Reducing readback frequency and bounded queued submission are subsequent
options. They need careful state/resource ordering, pending-pixel handling,
MPFR regression comparisons, and staged validation. This increment implements
neither, so existing safety and numerical behavior remain unchanged.

## Artifacts And Regression Coverage

Raw result: `stage-1920-20260916-232155-6432.jsonl` under
`tests/RendererChecks/bin/Release/net8.0-windows/scaling-results`.
Journal: `dispatch-6432-20260916-232155.jsonl` under the sibling
`dispatch-logs` directory. The result stores its absolute journal path and
all unrounded timing values. Existing `bin/` exclusions cover these artifacts.

GPU-free tests check exclusive accounting, inclusive envelope exclusion,
signed residuals, and journal timing containment/durable readback. Full-render
fixtures cover all three precision modes and check finite nonnegative timers,
two writes per successful dispatch, validation timing coverage, and timing
envelope containment. No hardware-specific speed thresholds are asserted.
