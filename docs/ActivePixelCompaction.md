> Historical experiment report. Active-pixel compaction and pixel-bound BLA have been removed. Their settings and compaction harness are no longer supported. Viewport BLA, its counters, and minimum-block profiling remain.

# Experimental DD Active-Pixel Compaction

## Status

Compaction is implemented, but disabled by default in both Release and Debug.
The measured reduction in requested work items did not yield a consistent frame
time improvement. Set `MANDELBROT_COMPACTION=1` to experiment with it; unset or
`0` retains the unmapped dispatch path. Other values are rejected.

This increment changes only double-double perturbation scheduling, not the
recurrence, precision selection, glitch criteria, reference selection, rebasing,
BLA acceptance rules, iteration budget, or submission safety limits.

## Implementation

At existing classification-readback checkpoints, the CPU builds a stable list
of pending batch-local slots. Subsequent DD shader invocations look up their
original slot through this list. Orbit state, pixel indices, classifications,
and per-pixel metrics remain in their original locations. This is dispatch-map
compaction, not physical state packing or GPU-side compaction.

The first slice of every batch uses identity addressing, ignoring stale map and
state contents. A map upload occurs only after outstanding submissions have
drained. Diagnostic journal groups are closed before changing dispatch size,
preserving their immutable workload metadata.

Packing requires both:

- At least `max(64, previousCount / 4)` requested work items have retired.
- The current ordering has at least twice as many occupied 64-thread shader
  groups as the packed ordering would, with at least two groups eliminated.

These are scheduling heuristics, not hardware occupancy measurements. The
64-thread grouping follows the shader declaration, not an assumed GPU warp size.

At the default 8192-pixel DD batch size, the GPU map is at most 32 KiB. Two CPU
maps total at most 64 KiB and are reused across the pass. The GPU map resource
exists even when compaction is disabled; disabled passes do not allocate the
CPU maps or scan/upload compaction lists.

The implementation does not merge survivors across batches, move the seven
double state records, or remove any scalar orbit iterations. Dispatch count,
readback cadence, and full original-batch classification readback sizes remain
unchanged. Only the requested work-item count of later dispatches can shrink.

Diagnostic counters report accepted compactions and requested DD work-item
invocations summed over slices. The latter is not a count of padded hardware
threads or a GPU timing measurement. These counters are disabled in ordinary
quiet Release rendering.

## Timing Results

Each setting ran in a separate Release test process, with one instrumented
baseline followed by five quiet warm frames. Both settings used BLA, 128
iterations per slice, readback interval 4, and at most two in-flight submissions.
The tests ran on the RTX 5080 with a 6912-iteration budget and scale `2^-40`.

| Fixture | Resolution | Disabled Mean | Enabled Mean | Disabled DD Requests | Enabled DD Requests |
| --- | --- | --- | --- | --- | --- |
| suspended-zoom | 1024x520 | 1150.67 ms | 1238.41 ms | 1,379,106 | 964,658 |
| original | 512x260 | 973.55 ms | 972.54 ms | 1,468,206 | 1,459,908 |

The first view made about 30% fewer DD requests but took about 7.6% longer.
The second view's approximately 0.1% timing difference is not evidence of a
useful speedup. Accepted compactions numbered 15 and 1 respectively.

Whole-image SHA256 hashes matched between enabled and disabled runs for both
views. Both settings used two references, with 109 repaired pixels in the first
view and 175 in the second, and zero unresolved pixels. Instrumented baselines
matched all 64 deterministic 768-bit MPFR samples. Quiet frames matched their
complete baseline image and performed no renderer log writes.

Evidence files under `tests/RendererChecks/bin/`:

- `compaction-strict-off-1024-a.jsonl`
- `compaction-strict-on-1024-a.jsonl`
- `compaction-strict-off-512-original.jsonl`
- `compaction-strict-on-512-original.jsonl`

These are small, warm fixed-view experiments, not balanced statistical studies
or isolated DD kernel measurements. No new deep 4K performance or external
driver-health validation was performed for this increment. GPU utilization,
hardware occupancy, and GPU timestamps were not measured. Indirection, state
access patterns, divergence, and CPU/upload overhead are possible explanations
for the slowdown, not established causes. Earlier less selective packing trials
were superseded by the occupied-group rule above.

## Regression Coverage

GPU-free checks cover stable slot ordering, untouched workspace tails, empty
and finished batches, retirement thresholds, occupied-group estimates, packing
thresholds, invalid workspace sizes, and opt-in configuration parsing.

GPU comparisons exercise all three acceleration settings, readback intervals
1/4/8, queue bounds 1/2, full and reversed sparse lists, batch boundaries, and
alternate references. Before each raw pass, state is poisoned with NaNs and map
entries with invalid indices. Enabled and disabled paths must produce identical
raw classifications and scalar/rebase/BLA counters, including glitch results.
Trusted raw samples are checked against 768-bit MPFR.

A complete paired render additionally requires identical repaired/colorized
images, precision mode, reference and repair counts, zero unresolved pixels,
and matching MPFR validation. Regression fixtures exercised 18 compactions and
586,584 fewer requested work items without changing the numerical results.

The final Release and Debug suites passed, each including quiet-production and
fence-lifetime checks and 17,604 submissions in 5,409 bounded diagnostic journal
groups. The 20-frame fence stress observed process handles 703 to 699 in Release
and 717 to 717 in Debug. Existing native-package NU1701 warnings remain.

## Further Work

Follow-up profiling below found no compelling low-risk optimization for this
dispatch-map design. Do not promote or expand it; keep the disabled default.
The opt-in implementation and checks remain available as experimental evidence.
Physical state packing, cross-batch survivor scheduling, and separate mapped
and unmapped shader variants would be distinct experiments requiring their own
performance and numerical validation, not straightforward CPU scan fixes.

## Follow-Up Profiling

The dedicated harness attaches a private `CompactionProfile` probe through
reflection. It works with ordinary renderer diagnostics explicitly disabled.
There is no application environment switch for the probe, and ordinary rendering
does not attach it, allocate its object, collect its counters, query its clocks,
or write its reports. The normal diagnostic result stays zero-valued.

The probe measures non-overlapping host-wall intervals for list construction,
packing decisions, map uploads, dispatch calls, and queue-drain/readback calls.
It also measures the inclusive DD loop and counts requested work, scanned slots,
dispatches, readbacks, compactions, and uploaded bytes. Dispatch calls include
recording/submission and queue backpressure. Drain/readback includes outstanding
GPU completion waits and the original classification copy/scan. Upload time can
include driver/transfer waits; it is not pure CPU execution time. None of these
measurements is a hardware GPU timestamp, occupancy measurement, or CPU sample.

After an MPFR-validated complete baseline and one warmup for each setting, the
harness uses three off/on/on/off blocks, for six measured frames per setting.
The same process and default device are reused; renderer objects are fresh for
each frame. It locks the renderer journal against writes, checks that diagnostic
results remain zero, and requires complete image/mode/reference/repair agreement
and zero unresolved pixels for every frame.

Both fixtures used 1024x520, budget 6912, scale `2^-40`, BLA, slices 128, readback
interval 4, and queue bound 2. Measured means:

| Measurement | suspended-zoom Off | suspended-zoom On | original Off | original On |
| --- | --- | --- | --- | --- |
| Complete frame | 1136.83 ms | 1227.23 ms | 3419.73 ms | 3434.28 ms |
| CPU list construction | 0 | 0.32 ms | 0 | 2.57 ms |
| CPU packing decisions | 0 | 0.25 ms | 0 | 1.73 ms |
| Map upload wall time | 0 | 7.39 ms | 0 | 3.46 ms |
| Dispatch-call wall time | 252.97 ms | 291.78 ms | 1011.65 ms | 1014.65 ms |
| Drain/readback wall time | 297.65 ms | 343.22 ms | 1201.11 ms | 1199.10 ms |
| Inclusive DD loop | 550.70 ms | 643.06 ms | 2213.24 ms | 2222.17 ms |
| Requested DD work items | 1,379,106 | 964,658 | 5,868,720 | 5,843,470 |

The suspended view had 56 scans and 15 compactions per enabled frame. CPU scan
and decision time totaled 0.57 ms. With uploads, direct compaction overhead
totaled 7.95 ms, while the full frame slowed by 90.40 ms (about 7.95%). Most of
the DD-loop increase (about 84.37 ms) was in dispatch/backpressure and completion/
readback intervals. Even eliminating all measured scan/decision/upload overhead
would not reverse this regression if those remaining costs stayed the same.

The original view had 196 scans and three compactions. Scan/decision/upload
overhead totaled 7.77 ms, while requested work fell by only about 0.43%. Its
0.43% slower frame is not a statistically established regression, but supplies
no evidence of an improvement either. Timing variation outside the DD loop
means complete-frame differences do not exactly equal loop differences.

This narrows the explanation: excessive CPU list calculation is not the main
source of the suspended-view regression. It does not distinguish shader memory
indirection, regrouped pixel divergence/locality, CPU submission costs, or driver
scheduling within the remaining envelope. No scalar orbit work was eliminated;
the saved requests were predominantly invocations that already exited early.
Optimizing scans alone therefore has no demonstrated useful payoff here.

Evidence: `tests/RendererChecks/bin/compaction-profile-1024-suspended.jsonl` and
`compaction-profile-1024-original.jsonl`. Each contains a passed completion and
exact-image checks for all 14 frames, including the two warmups. The probe adds
clock overhead, so these are targeted comparative measurements rather than
production frame-time promises. Their first-view result agrees with the earlier
unprobed slowdown. There is no external Windows health monitor or new deep 4K
validation in these experiments.

Reproduce one bounded experiment after the mandatory matching regression suite:

```powershell
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --compaction-profile 1024 suspended-zoom ./tests/RendererChecks/bin/compaction-profile-new.jsonl
```

Use a new report filename to avoid mixing runs. The dedicated mode accepts the
existing scaling widths only up to 1920 and stops on the first failure without
retrying or changing safety limits. Regression compaction cases attach and check
the probe as well, requiring finite nonnegative intervals, stages within their
loop envelope, and no scans/uploads when compaction is disabled.

After adding the probe and dedicated mode, complete Release and Debug suites
both passed again: each validated 17,604 submissions in 5,409 bounded journal
groups, numerical equivalence, quiet production, and 20-frame fence stress.
Handle counts stayed 698 to 698 in Release and 707 to 707 in Debug. Existing
NU1701 native-package warnings remain; no new deep 4K health claim is implied.
