# Submission safety timing and suspended UI

The reported screenshot showed `GPU batch took 1,094 ms. Rendering suspended.`
at approximately center (-0.46729724795644717, 0.5881255899137422), scale 2^-40.
This message means the application safety brake fired; it is not a numerical
zoom-depth limit. The old suspended state deliberately required an application
restart, but Reset could still replace the bitmap before rendering returned
without doing any work, leaving a black image.

## Confirmed timing defect

The old dispatch clock started before CreateComputeContext/For and was read
when the render thread consumed a queued completion. It therefore included
first-use pipeline creation, later host shader recording, and any delay between
actual completion and FIFO consumption. A host stall could be classified as
an overlong GPU batch even when GPU work had completed promptly.

Without a trace or journal from the reported session, the screenshot's 1094 ms
cannot be apportioned between host and GPU work. The change corrects this
demonstrable timing defect; it does not assert that every overlong completion
is a false alarm.

## Corrected measurement

All dispatches now record an explicit ComputeContext before the safety clock
starts. Synchronous timing surrounds Dispose (submission and fence wait).
Asynchronous timing starts immediately before DisposeAsync and is captured by
an ExecuteSynchronously task continuation, before the render thread consumes
the result. An observer task preserves native exceptions and ensures its
timestamp is published before the queue's completion callback reads it.

The queue still has at most two in-flight submissions. This is not a second
GPU queue, Task.Run, a GPU reset, or an automatic retry. Existing failure drain,
resource ownership, barriers, pixel limits and iteration slices are preserved.
The threshold is still 1000 ms. Queue residency and native completion-callback
latency remain included deliberately. The measurement is a monotonic host
submission-to-observed-completion upper bound, not a hardware GPU timestamp;
severe OS/continuation scheduling delays can still affect it. The brake only
stops future work, not a submission already in progress.

Diagnostic stage accounting still includes host recording costs. The reported
maximum dispatch duration and the brake now use completion latency instead
of recording-to-late-consumption time, so maxima from old/new builds are not
directly comparable. Quiet Release does not collect profiling counters or
renderer logs; the safety clock and bounded completion observer remain active.

## Suspended UI

On a real safety failure, the UI stops the resize timer, clears queued work,
disables Reset/Back, and explains that an application restart is required.
Click, Reset, Back, and resize handlers also guard suspension before changing
the viewport or bitmap. The last image remains visible. Suspension is not
cleared automatically because a timing or device failure has not established
that further GPU submissions are safe.

## Regression coverage

- Deterministic injected-clock tests exclude five seconds of cold recording and
  two seconds of delayed FIFO consumption from a 20 ms submission, while a
  genuine 1094 ms completion still meets the unchanged safety threshold.
- Existing queue tests retain FIFO/backpressure, failure identity, native
  device status, tail completion draining and no resubmission after failure.
- GPU-free STA WPF tests instantiate an unshown window, allocate its bitmap,
  and verify that suspended Reset/Back/click/resize handlers preserve bitmap
  and viewport identity and neither start nor queue rendering.
- Raw FP64/DD comparisons and complete pipeline checks cover the approximate
  screenshot center at scale 2^-40 (6912 iterations) and the next same-center
  zoom at scale 2^-42 (7232 iterations), against 768-bit MPFR. The undisplayed
  full-precision center and actual next clicked center are not available.
- Existing native wait, sparse dispatch, batch seams, buffer reuse, BLA,
  rebasing, diagnostics-off and full rendering/repair regressions still run.

## Monitored fixtures

The test executable and external monitor accept explicitly selected fixtures:
`original`, `suspended-zoom`, and `suspended-zoom-next`. Alternate fixtures are
test-only; the shipping application does not read a fixture configuration.
Unknown names are rejected before device creation. Each invocation selects
one resolution and one fixture; there is no automatic stage progression.

```powershell
./tools/ValidateProduction.ps1 -Stage 256 -Fixture suspended-zoom
./tools/ValidateProduction.ps1 -Stage 3804 -Fixture suspended-zoom
./tools/ValidateProduction.ps1 -Stage 3804 -Fixture suspended-zoom-next
```

Use the already regression-validated Release executable, Windows elevation for
protected dump/event inventories, and fresh evidence directories. Review each
stage before advancing. These fixtures check a baseline against 64 MPFR samples,
then exact complete production-image/classification comparisons with renderer
logging disabled, plus external events, dumps and process resource samples.
Completed results are appended below; examples alone are not passing results.

## Completed validation

Full Release and Debug regression suites passed after the final renderer and
test changes. Each checked 16964 journaled submissions in 5241 bounded groups,
including the new timing and suspended UI tests. The twenty-frame quiet-render
handle stress passed at 696 to 696 handles in Release and 697 to 699 in Debug.
Existing native-package NU1701 warnings remain. Both executables were rebuilt;
no driver reset, watchdog change, automatic retry or threshold increase was used.

All explicitly selected monitored stages below passed, with child and elevated
monitor exit codes zero, exact quiet images/classifications, zero unresolved
pixels, all 64 baseline MPFR samples matching, and no new relevant Windows
events or changed dumps. Both 4K stderr files were empty.

| Fixture | Size | Budget | Baseline (ms) | Quiet (ms) | Repaired | Longest observed submission completion (ms) |
| --- | --- | --- | --- | --- | --- | --- |
| suspended-zoom | 256x130 | 6912 | 763.9203 | 221.6408 | 7 | 18.5371 |
| suspended-zoom | 3804x1932 | 6912 | 23528.5539 | 14730.8168 | 1484 | 27.6776 |
| suspended-zoom-next | 3804x1932 | 7232 | 22425.0936 | 13016.3280 | 1279 | 29.5801 |

All three final modes were PerturbationDoubleDouble, exercising fallback beyond
the DirectFloat boundary. At 4K, post-initialization sampled handles ranged from
615 to 1034 (last live sample 1034) at the screenshot scale, and 629 to 1134
(last live sample 910) at the next same-center zoom. The smoke test finished
too quickly for post-initialization two-second handle samples. These are not
exact frame-end counts or hardware GPU execution times. No suspension occurred.

Evidence directories under `tests/RendererChecks/bin/production-health/`:

- `submission-timing-256-suspended/`
- `submission-timing-3804-suspended/`
- `submission-timing-3804-suspended-next/`

Each contains frame progress, external health records, the installed-binary
wait audit, and stdout/stderr. Baseline-first warm-cache timings are not a
controlled speedup comparison. The screenshot's original 1094 ms remains
unattributed without its original session trace, and these approximate-center
checks do not reproduce the entire interactive click sequence.
