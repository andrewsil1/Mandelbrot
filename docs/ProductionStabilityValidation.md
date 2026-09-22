# Quiet Release Stability Validation

## Current outcome: local patch validated through a five-frame 4K repeat

The pinned runtime repair passed full Release/Debug regressions, the elevated
five-frame smoke, the original 2880x1460 accumulation workload, a single
3804x1932 baseline-plus-quiet workload, and a separate five-frame 3804x1932 repeat.
Native handle counts stayed bounded in these tests. Longer endurance and
interactive transition/resize work have not yet been performed with this patch.
This does not diagnose or prove resolution of the historical kernel crash.
Patch details and new evidence are below.

A subsequent [submission safety timing correction](SubmissionSafetyTiming.md)
addresses host recording/late FIFO delays being misclassified as GPU batch
duration, plus Reset blanking the bitmap after suspension. Its final full
Release/Debug suites and monitored 4K approximate screenshot/next-depth fixtures
passed separately; see that document for measurements and limitations. The
native wait patch and one-second safety threshold were not changed.

## Initial outcome: stopped before 4K (stock runtime)

The increment added durable test-only frame progress, one-to-five quiet frames
on a shared renderer/device, and an external health monitor. Shipping Release
instrumentation and the GPU dispatch/concurrency limits were not changed.

The full Release and Debug renderer regression suites passed, each validating
16396 instrumented submissions in 5170 journal groups and the existing quiet
Direct/FP64/DD comparisons. Health event/progress/record fixtures passed in
PowerShell 7 and 5.1. The control fixtures passed in both hosts: repeat counts
0/6 are rejected and an existing stop request produces durable failure evidence
before device creation. PowerShell 5.1 used a process-local execution-policy
override for these unsigned local fixtures; no system policy was changed.

An elevated external-monitor smoke run at 256x130 passed two quiet frames:
303.9614 and 301.6009 ms, identical baseline images, 62 repaired pixels per
frame, zero unresolved, and zero new relevant events or changed dumps. This
was completed before the subsequent static lifetime gate was added.

The 2880x1460 original failure-view fixture used center
(-0.34562335012588691, 0.625450590999726), scale 2^-40, 6912 iterations,
128-iteration slices, four-slice readback cadence, BLA, and two in-flight
submissions on the existing compute queue. The RTX 5080 was used, adapter LUID
104255. Windows build was 26200; the NVIDIA driver version recorded by CIM was
32.0.16.1074. These are observed identities, not recommendations about versions.

| Measurement | Result |
| --- | --- |
| Instrumented baseline | 35826.1567 ms |
| Quiet Release frame | 27812.6067 ms |
| Baseline 768-bit MPFR samples | 64, zero mismatches/unresolved |
| Whole-image/status comparison | Exact match |
| Repaired / unresolved pixels | 6105 / 0 |
| Quiet automatic journal writes | None; journal locked against writes |
| Child exit code | 0 |
| New relevant Windows events / changed dumps | 0 / 0 |

These image and OS-event checks passed, but **the resource-lifetime review did
not pass**. The monitor sampled 13354 handles immediately before baseline
completion and 26093 near quiet-frame completion, an additional 12739 handles.
Counts climbed progressively during GPU submission rather than returning to a
small bounded level. The process initially had 30 handles before initialization.
Private bytes were 169738240 in the pre-completion baseline sample and
259981312 in the final quiet sample. Managed memory alone is not proof of a
leak; the native wait-registration defect below supplies independent evidence.

Evidence directory:
`tests/RendererChecks/bin/production-health/stage-2880-elevated/`
contains `health.jsonl`, `frames.jsonl`, stdout/stderr, and
`async-fence-audit.json`. The original health completion record predates the
static audit and covers its then-existing image/event checks only. It must not
be interpreted as passing the later native-lifetime review. The tests were
baseline-first/warm-cache; timing differences are not controlled benchmarks.

## Confirmed pinned-library lifetime defect

Read the actual installed ComputeSharp 3.2.0 binary without executing its GPU
methods:

```powershell
./tools/AuditAsyncFenceLifetime.ps1
```

Its `WaitForFenceAsync` calls `RegisterWaitForSingleObject`. The corresponding
completion callback closes the event and frees the callback context, but neither
method calls `UnregisterWait` or `UnregisterWaitEx`. The v3.2.0 source additionally
shows that the registration handle is only a local variable, is not retained for
cleanup, and registration uses flags zero, not `WT_EXECUTEONLYONCE`.

Microsoft requires registered waits to be cancelled explicitly, including
one-shot waits. Closing a waited-on handle while its wait is still pending has
undefined behavior. The missing cancellation is therefore a concrete API
lifetime defect; the sampled accumulation is consistent with it. This is not
proof that it caused the older kernel crash, nor that it is the only defect.

- [Pinned ComputeSharp execution source](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)
- [RegisterWaitForSingleObject lifetime contract](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerwaitforsingleobject)

Inspected assembly: version 3.2.0.0, SHA256
`C06FB300AF75CB12E41875CA00EA176F1CA1BE0B697B741F6C9606A3F2D65240`.
The binary audit records resolved IL call targets and fails the necessary
unregister-call check. Merely finding such a call in a future binary will not
prove correct races or error-path cleanup; changed implementations require
source-level review, including any helper to which cleanup moves.

## Initial gate and repair plan (before the local patch)

`ValidateProduction.ps1` now runs the static audit before starting its child.
An explicit test of this gate exited 1, recorded failure, and launched no GPU
work: `tests/RendererChecks/bin/production-health/preflight-blocked/`.
Protected dump access also fails closed; genuine Windows elevation was needed
for the earlier live monitoring. No watchdog settings, GPU resets, automatic
retries, force-kills, or higher-resolution runs were used.

The original 3804x1932 quiet pass and five-frame repeat were deliberately not
started after discovering the defect in the stock runtime. At this point in
the initial review they were blocked, not passed. Later patched results are
recorded below; the stock-runtime stages were not retried.

Before continuing asynchronous validation, use a reviewed fixed dependency
or a pinned source patch that retains and cancels the native wait registration,
handles callbacks that race registration's return, and releases event/context/
GCHandle/device references on every failure path. Changing only the one-shot
flag is insufficient. Do not patch private command-list fields or inject an
unregister call into an installed DLL at runtime.

The existing synchronous setting (`MANDELBROT_INFLIGHT=1`) avoids this async
path and is a possible temporary mitigation; it has regression coverage but
has not been run through this new external high-resolution stability matrix.
After fixing the dependency, rerun full regressions and the smoke/2880 stages,
then the original 4K single pass, and only then the bounded five-frame repeat.
Longer endurance, interactive resize/zoom transitions, D3D12 debug validation,
and DRED remain separate confidence-building work.

## Pinned local runtime repair

The application now replaces only the ComputeSharp runtime with a source build
of v3.2.0 plus the wait-lifetime patch. Released 3.2.0 generators and Core are
unchanged. See [the dependency notes](../third_party/README.md) for provenance,
cleanup ordering, failure-path limitations, and the exported patch.

Full Release and Debug regression suites passed after the final patch. Both
configurations passed all numerical/dispatch/queue tests and the new mandatory
wait-lifetime checks:

| Check | Release | Debug |
| --- | --- | --- |
| Publication races | 10000, one cleanup owner each | Same |
| Native wait registrations | 2000; handles 269 to 269 | 2000; handles 276 to 276 |
| Forced callback orderings | Both passed | Both passed |
| Native last-error capture | ERROR_INVALID_HANDLE (6) | Same |
| Twenty quiet renderer frames | Exact images, zero unresolved; handles 609 to 609 | Exact images, zero unresolved; handles 618 to 618 |
| Loaded runtime marker | mandelbrot.waitfix.1 | Same |

The installed-binary audit now sees UnregisterWaitEx in the cleanup helper and
passes its necessary-call gate. This is supported by source review and native
probes, not treated as sufficient proof on its own. Allocation/device-removal
failures are not injected by these tests. Unexpected cancellation failures
quarantine native resources and fault the awaiter rather than risk premature
release.

Monitored smoke and higher-resolution results are recorded separately below
when completed. The earlier unmodified-library failures remain historical
evidence, not results for this patched binary. The original kernel crash is
not established to have been caused by the missing wait cancellation.

### Patched monitored smoke and 2880 stage

`tests/RendererChecks/bin/production-health/patched-256/` passed an instrumented
256x130 baseline and five quiet frames. All quiet images matched exactly, with
zero unresolved pixels and no new relevant Windows events or changed dumps.
Two-second external sampling is too coarse to characterize native handle
behavior in these short frames; the native and twenty-frame tests above cover
that separately.

`tests/RendererChecks/bin/production-health/patched-2880/` passed the same
2880x1460 workload used for the original accumulation observation:

| Measurement | Patched result |
| --- | --- |
| Instrumented baseline | 35428.3332 ms |
| Quiet Release frame | 27811.8538 ms |
| Baseline 768-bit MPFR | 64 samples, zero mismatches/unresolved |
| Quiet whole image / statuses | Exact match |
| Repaired / unresolved | 6105 / 0 |
| Post-initialization sampled handle range | 508 to 1009 |
| Final live handle sample | 819 |
| New relevant Windows events / changed dumps | 0 / 0 |
| Renderer and monitor exit codes | 0 / 0 |

These are process-wide sampled counts, not exact per-registration accounting.
The previous progressive growth into tens of thousands was not observed in
this run. Timing was essentially unchanged from the older quiet frame;
baseline-first warm-cache measurements are not controlled benchmarks.

### Upstream report

[ComputeSharp issue #936](https://github.com/Sergio0694/ComputeSharp/issues/936)
reports the source-level wait-lifetime defect with public code and Win32
documentation links. Publication review rejected the detailed diagnostic
payload; the submitted source-only report contains no local machine, workload,
performance, logging, or patch implementation details. The detailed evidence
remains in this repository and requires approval before external publication.

### Patched monitored 4K single pass

`tests/RendererChecks/bin/production-health/patched-3804-single/` passed one
instrumented baseline followed by one quiet Release frame at 3804x1932, using
the same fixture, iteration budget, slice/readback cadence, and two-context
queue limit as the 2880 stage. No driver or watchdog settings were changed.

| Measurement | Patched result |
| --- | --- |
| Instrumented baseline | 61669.7381 ms |
| Quiet Release frame | 48661.2273 ms |
| Baseline 768-bit MPFR | 64 samples, zero mismatches/unresolved |
| Quiet whole image / statuses | Exact match |
| Repaired / unresolved | 10570 / 0 |
| Post-initialization sampled handle range | 511 to 1252 |
| Final live handle sample | 723 |
| New relevant Windows events / changed dumps | 0 / 0 |
| Renderer and monitor exit codes | 0 / 0 |

Process-wide handles fell from their peak rather than showing the previous
progressive accumulation. The external monitor does not measure GPU utilization
or VRAM. A single baseline-plus-quiet test is not endurance validation. The
subsequent five-frame repeat is recorded below. Longer endurance and interactive
transition/resize checks remain; no automatic stage progression is enabled.

### Patched monitored five-frame 4K repeat

`tests/RendererChecks/bin/production-health/patched-3804-repeat5/` passed a
separate instrumented baseline followed by five quiet Release frames on the
same renderer/device at 3804x1932. The original failure-view fixture, 6912-iteration
budget, 128-iteration slices, four-slice readback cadence, BLA, and two-context
queue limit were unchanged. The already regression-validated Release binary
was used without rebuilding or modifying the renderer.

The baseline took 61329.7248 ms and passed all 64 deterministic 768-bit MPFR
samples with zero mismatches/unresolved. Each quiet frame matched the entire
baseline image and classifications, including 10570 repaired pixels and zero
unresolved pixels. Metrics, validation sampling, timers, and renderer logging
were disabled for those frames; journal size remained 23275833 bytes while
locked against writes.

| Quiet frame | Render time (ms) | Sampled handle range | Last in-frame handle sample |
| --- | --- | --- | --- |
| 1 | 48580.5824 | 770 to 1232 | 781 |
| 2 | 48553.9145 | 761 to 1288 | 823 |
| 3 | 48730.2227 | 605 to 1242 | 830 |
| 4 | 48787.2481 | 778 to 1416 | 841 |
| 5 | 48777.0135 | 759 to 1047 | 831 |

Across the whole post-initialization run, sampled handles ranged from 530 to
1416 and finished at 831. Private bytes peaked at 382349312 and the final live
sample was 313409536. The last private-byte samples in frames 2 through 5 were
311422976, 313036800, 311812096, and 313409536, respectively. Neither those
late-frame samples nor native handles exhibited the previous progressive
per-submission accumulation. These are process-wide samples at approximately
two-second intervals, not exact frame-end counts or a formal leak proof.

The child and elevated monitor both exited zero. Monitoring, including its
post-exit reporting delay, finished in 318.1086326 seconds with zero new relevant
Windows events and no changed dumps; stderr was empty. The GPU-free preflight
also passed 10000 publication races and 2000 native waits (handles 286 to 286),
and confirmed the patched runtime marker and necessary unregister call.

This establishes repeatability for this fixture and bounded run, not universal
driver stability or resolution of the historical kernel crash. The monitor
does not measure GPU utilization or VRAM, and no driver/watchdog settings were
changed. The next confidence steps are a longer bounded endurance run and
interactive zoom/resize transitions, with debug-layer/DRED work kept separate.
