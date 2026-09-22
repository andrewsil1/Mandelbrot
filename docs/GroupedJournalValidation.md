# Bounded Journal Checkpoint Validation

On 2026-09-16, durable journal writes were amortized over at most eight
consecutive slices within one pixel batch/reference/mode. GPU slice length,
pixel limits, synchronous completion, per-slice readback/pending scans, and
the one-second completed-dispatch brake were unchanged. Direct rendering
continues durable per-dispatch begin/end writes.

## Crash-Evidence Policy

Schema 2 writes a durable `group-begin` before the first dispatch. It contains
the planned slice range, maximum submission count, and complete first-slice
context (render/adapter/reference/resources/mode/batch/budget). Each GPU slice
still has an individual submission ID and begin/end records, but these records
are buffered and flushed together with `group-end` at a checkpoint.

Checkpoints occur after eight slices, when a 100 ms elapsed target is reached,
or on early/batch completion. The time target is checked after synchronous GPU
work and readback; it does not preempt either operation and is not a hard
100 ms bound. Groups cannot cross batch, mode, or reference boundaries.

Failures and the diagnostic brake flush `group-failed`/`group-stopped` evidence
when possible. Dispose performs best-effort `group-aborted` recording only for
pending exception-path records; it preserves the original exception and never
retries an already-failed checkpoint. A normal intent/checkpoint write error
aborts rather than silently allowing more GPU submissions.

After a kernel crash, a durable unmatched intent identifies the authorized
group but cannot identify which slices actually ran or finished. Missing slice
records do not mean that the GPU never executed them. Partial/malformed trailing
records also imply an incomplete run. This explicitly trades exact per-slice
crash progress for lower write overhead while preserving durable workload intent.
No logging-disabled option, whole-frame-only flush, watchdog registry change,
larger GPU dispatch, or automatic recovery/retry was introduced.

## Regression Coverage

The full Release suite passed. Added GPU-free tests cover default/valid/invalid
group limits, the checkpoint-time boundary, durable intent before authorization,
buffered evidence visibility, the eight-slice bound, partial completion,
failure/stop/abort records, idempotent disposal, locked-file checkpoint failure,
failed intent authorization, and no cleanup retry after a failed write.

FP64 and DD fixtures compare one-slice and eight-slice journal groups: identical
images, modes, reference/repair/unresolved counts, and GPU dispatch counts,
with matching MPFR samples and fewer checkpoints. Existing raw 768-bit MPFR,
rebasing/BLA, slice resume/state reuse, sparse/batch seam, adaptive repair,
pipeline, and dispatch-limit checks also passed.

Timing checks count durable writes separately from JSON records/groups and
retain exclusive interval accounting. The successful-journal validator checks
preceding durable intents, maximum slice count/range, sequential completion,
batch/reference/mode isolation, and complete matching submissions/groups.

## Measured Result

One fresh-process Release run at 1920x980 used the same displayed-coordinate
failure-view fixture, 6912 iterations, 128-iteration slices, BLA requested,
metrics disabled, and 64-sample 768-bit MPFR validation. Adapter: NVIDIA
GeForce RTX 5080. Compare with the immediately preceding instrumented profile:

| Measurement | Previous | Grouped |
| --- | ---: | ---: |
| Standalone stage time (s) | 36.044 | 16.802 |
| Renderer time (s) | 35.843 | 16.602 |
| Journal time (s) | 21.843 | 3.082 |
| Dispatch/completion time (s) | 11.224 | 10.916 |
| Durable write calls | 11340 | 1470 |
| GPU dispatches | 5670 | 5670 |
| Longest dispatch (ms) | 12.252 | 12.608 |

This comparison is a 2.15x stage speedup and an 85.89% reduction in journal
time, not a repeated benchmark or a hardware-independent guarantee. The
small dispatch-time change is not evidence of changed shader performance.
The grouped run had 735 journal groups and 12810 JSON records; every group
and submission matched its intent/completion. Two references and 2749 repaired
pixels matched the previous run, with zero unresolved pixels and zero MPFR
mismatches among 64 samples. Readback took 0.463 s, repair 1.567 s, validation
0.423 s, and remaining host time 0.052 s.

FP64/DD dispatch shares of their inclusive slice-loop host time rose from
18.81%/45.27% to 58.66%/83.00%. These are host-time ratios, not measured GPU
utilization or occupancy; they show smaller CPU/file gaps without larger GPU
submissions. No System warning/error/critical events or Application events
1000/1001 were found after stage start. Absence of such events is not proof
of driver health or resolution of the original 0x119 paging-command failure.

4K was not rerun in the implementation increment. The later authorized
larger-stage validation is recorded below. Larger-workload validation should
again proceed one stage at a time, with numerical/journal/system checks between
stages and no automatic retry. Readback frequency and asynchronous submission
remain unchanged and are separate future implementation decisions.

## Artifacts

Result: `stage-1920-20260916-233045-39744.jsonl` under
`tests/RendererChecks/bin/Release/net8.0-windows/scaling-results`.
Journal: `dispatch-39744-20260916-233045.jsonl` under the sibling `dispatch-logs`
directory. The result retains the absolute journal path and unrounded timings.
Generated journal lifecycle-test files are in `journal-checks` under the test
output. All generated artifacts remain covered by the existing `bin/` ignore.

## Larger-Resolution Follow-Up

On 2026-09-16 the full Release regression suite passed again, then 2880x1460
and 3804x1932 each ran once in a fresh process. The intermediate stage's
numerical results, grouped journal, and Windows diagnostic queries were
reviewed before advancing to nearly-4K. No automatic retries, configuration
changes, renderer edits, or enlarged GPU workloads were used. Both stages
retained the same displayed-coordinate fixture and settings as the 1920 run.

| Resolution | Previous stage time (s) | Grouped stage time (s) | Dispatch (s) | Journal (s) | Longest dispatch (ms) | Dispatches | Groups |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2880x1460 | 79.480 | 37.059 | 24.674 | 6.931 | 13.063 | 12636 | 1638 |
| 3804x1932 | 139.367 | 63.995 | 43.216 | 11.972 | 14.782 | 22032 | 2856 |

The single-run comparisons show 2.14x and 2.18x stage speedups respectively,
not a repeated benchmark or hardware-independent guarantee. Dispatch counts
are identical to the corresponding per-slice durable-logging runs. Both runs
used the NVIDIA GeForce RTX 5080, ended in PerturbationDoubleDouble mode, and
used two references. Repaired counts were 6105 and 10570, matching the earlier
runs. Both had zero unresolved pixels and zero mismatches among 64 deterministic
768-bit MPFR samples.

The journal validator found preceding durable intents, authorized ranges/counts,
batch/reference/mode isolation, and matching completions for every submission
and group. There were 3276/5712 durable write calls and 28548/49776 JSON records
respectively. Neither run triggered the completed-dispatch brake. System queries
for warning/error/critical events and Application queries for events 1000/1001
found no entries from 23:34:51 UTC through the post-run review. Query diagnostics
reported only the expected NoMatchingEventsFound result, not an access error.

At nearly-4K the renderer itself took 63.798 s, including 1.799 s readback,
5.950 s MPFR repair, 0.436 s sample validation, 0.218 s coloring, and 0.163 s
remaining host overhead. FP64/DD dispatch shares of their inclusive slice-loop
host time were 58.46%/83.48% (58.06%/83.11% at 2880). These are host-time
ratios, not measured GPU utilization or occupancy. There was no new GPU
telemetry capture in this follow-up.

This establishes successful staged runs with grouped journaling through the
displayed-coordinate nearly-4K failure case. It does not recover undisplayed
original MPFR coordinates, establish all-pixel correctness or repeat-run
stability, or prove the original 0x119 paging-command failure is fixed.

Additional results under `scaling-results`:

- `stage-2880-20260916-233451-8856.jsonl`
- `stage-3804-20260916-233559-20104.jsonl`

Their journals under `dispatch-logs` are `dispatch-8856-20260916-233451.jsonl`
and `dispatch-20104-20260916-233559.jsonl`. Result records retain the full paths
and unrounded measurements. The next throughput increment can investigate
less frequent readback at these unchanged slice/work limits; queued GPU
execution and larger workloads remain separate, higher-risk changes.

The subsequent delayed-readback implementation and 1920x980 profile are in
[DelayedReadbackValidation.md](DelayedReadbackValidation.md). That change
reduced copies from 5670 to 1575 but did not establish an end-to-end speedup
in its single comparison. The larger grouped-journal results above predate
that readback-cadence and explicit ordering change.
