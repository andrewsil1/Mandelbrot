# Release Diagnostic Policy

**Dependency lifetime repair:** external validation found native handle
accumulation and an unregistered asynchronous fence wait in stock ComputeSharp
3.2.0. The application now builds a pinned local runtime patch. See
[ProductionStabilityValidation.md](ProductionStabilityValidation.md) for staged
validation results and [the dependency notes](../third_party/README.md) for its
scope. Disabling diagnostics alone does not correct this library defect.

Ordinary Release rendering defaults to diagnostic collection disabled. Debug
defaults to enabled. `MANDELBROT_DIAGNOSTICS=1` explicitly enables collection for
regression testing or profiling; `0` disables it in either configuration.
Other values are rejected. The renderer captures this policy at construction,
so a render uses a consistent diagnostic setting.

With diagnostics disabled, the renderer does not create dispatch/group journal
objects, allocate JSON metadata or submission GUIDs, serialize records, write
files, flush checkpoints, or pause the queue for journal checkpoints. Detailed
stage/wait timers and counters are disabled, GPU metric buffers use only the
required unused shader-binding placeholder, MPFR validation sampling is skipped,
and the detailed timing tooltip is absent. Legacy `MANDELBROT_METRICS=1` and
`MANDELBROT_VALIDATE=1` alone cannot enable collection in ordinary Release.
RenderTimings remains in the result contract, with zero-valued measurements.

The whole-frame elapsed time shown in the status bar remains available. Repair,
reference, precision-mode, and unresolved-pixel counts are functional render
results, not diagnostic sampling, and remain visible.

Safety and correctness mechanisms are unchanged: two-submission queue bound,
pixel/iteration limits, readback cadence, UAV barriers, completion draining,
resource lifetimes, device-removal queries, glitch detection, multi-reference
retries, and bounded MPFR repair. A lightweight allocation-free duration clock
also remains for the one-second diagnostic brake. That brake still stops further
submissions after an abnormally slow completion, without requiring a writable
log. It does not preempt GPU work or guarantee watchdog safety.

The later [submission timing correction](SubmissionSafetyTiming.md) excludes
cold pipeline recording and delayed FIFO consumption from that brake. Async
completion observers capture the clock and propagate failures even in quiet
Release; these small safety tasks are not profiling or renderer logging.
The one-second threshold and workload/concurrency limits remain unchanged.

## Regression Coverage

The RendererChecks executable explicitly enables diagnostics for its existing
timing/journal/MPFR assertions in both configurations. This changes only the
test process, not the application binary or the calling shell's environment.
The full suite additionally compares diagnostics-on and diagnostics-off renders
for DirectFloat64, perturbation FP64, and perturbation DD, with queue settings
1 and 2. Release comparisons use the shipped unset-variable default; Debug
comparisons explicitly disable diagnostics.

Production comparisons require identical whole images, precision modes,
reference/repair/unresolved counts, zero diagnostic measurements, disabled GPU
metrics, and no automatic validation sampling. The log is held open without
write sharing during production rendering: any automatic logging attempt would
fail the test. The existing MPFR-validated baseline supplies numerical evidence
for the identical output. No GPU reset or watchdog registry change is involved.

For every application build, run the mandatory complete matching-configuration
regressions before reporting it ready:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

An additional comparison mode runs the historical failure-view fixture at one
explicitly requested resolution and compares complete images:

```powershell
tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --production-stage 1920
```

Supported widths follow the existing scaling stages (256 through 3804). Review
smaller stages before advancing; there are no automatic retries or stage loops.
The mode first renders an instrumented, MPFR-sampled baseline, then a production
frame under the unwritable-log lock. Its ignored JSON report contains measured
wall times and log byte counts. This is a same-process, baseline-first comparison;
production benefits from warmed shader caches and omits validation sampling, so
it must not be presented as a general or statistically controlled speedup.

Existing Nsight tools intentionally target the diagnostic test executable;
ordinary application rendering remains uninstrumented unless explicitly opted
in. Prior diagnostic log files are not deleted by this change.

## External Production Health Validation

After the complete Release regressions pass, run one explicit stage at a time:

```powershell
./tools/tests/TestProductionHealth.ps1
./tools/tests/TestProductionControl.ps1
./tools/ValidateProduction.ps1 -Stage 2880
./tools/ValidateProduction.ps1 -Stage 3804
# Only after reviewing both single-stage results:
./tools/ValidateProduction.ps1 -Stage 3804 -QuietFrames 5
```

Each invocation starts a fresh Release test process. It first renders one
instrumented baseline checked against 64 deterministic 768-bit MPFR samples,
then one to five quiet frames on a shared renderer/device. Every quiet image
and mode/reference/repair/unresolved count must match the complete baseline.
The baseline and every quiet frame must have zero unresolved pixels. Diagnostic
measurements must remain zero and automatic validation/metrics disabled in the
quiet frames. The original journal remains locked against writes during each
quiet frame. A repeated run is baseline-first and shader-cache-warm, not a cold
first-frame benchmark or an interactive/long-duration endurance test.

The test executable writes durable frame-boundary progress to `frames.jsonl`.
The external PowerShell monitor writes `health.jsonl`, captures stdout/stderr,
records OS/driver identities and dump inventories, and samples process working
set/private bytes/handles every two seconds. It checks System/Application logs
for display-driver errors, renderer crashes, relevant WER/kernel reports, and
new or modified live-kernel/minidump/full-kernel dumps. Reporting is allowed
ten seconds to settle after the process exits. Memory sampling is not a VRAM,
temperature, GPU utilization, or GPU execution-time measurement.

Health access failures stop the test rather than silently reducing coverage.
Dump inventory may require elevation; no dump contents are read. An adverse
event, dump change, or monitor failure requests a stop before the next frame.
That request cannot preempt an active render or a hung driver. There is no GPU
reset, force-kill, watchdog registry modification, retry, or automatic stage
progression. A durable start without completion is not a pass. Late events
outside the recording window, machine reboot, and interactive responsiveness
still require manual review. Passing this bounded matrix is evidence against
recurrence, not proof that the historical driver failure's root cause is fixed.

The wrapper also audits the installed ComputeSharp binary's native asynchronous
wait-registration path before launch. An absent unregister call fails closed.
This is a necessary static check for the pinned library, not proof that a future
replacement has correct callback races or failure cleanup. The binary audit
requires PowerShell 7 so it can load the .NET 8 assembly without creating a GPU
device. The independent event/progress fixtures support PowerShell 5.1 and 7.

GPU-free health fixtures cover event/failure classification, incomplete progress,
instrumented/corrupt output rejection, and durable-record roundtrips. They do
not substitute for live GPU tests or the mandatory renderer regressions.

## Validation Results

Complete Release and Debug suites passed, each checking 16396 instrumented
submissions in 5170 journal groups plus the diagnostics-off comparisons.
Nsight measurement/parser fixtures and capture child-exit checks also passed.
Existing native-package NU1701 warnings remain.

The additional Release 1920x980 failure-view comparison passed: diagnostics-on
16792.9 ms, ordinary production 12368.7 ms, identical complete images, 2749
repaired pixels, zero unresolved. Its baseline matched all 64 MPFR samples.
The production frame did not change the log's byte count while that file was
locked against writes. This approximately 26% difference is a single
baseline-first, same-process measurement, not a controlled speedup claim:
production also omits MPFR sampling and benefits from warmed shader caches.

Evidence: `tests/RendererChecks/bin/Release/net8.0-windows/scaling-results/production-1920-20260917-041150-46724.json`.
No new diagnostics-off deep near-4K run was performed for the diagnostic-policy increment;
mandatory dispatch-limit tests cover 4K, and the earlier deep 4K captures used
diagnostics. Those successful runs do not prove the historical kernel crash is
fixed.

Subsequent pinned-runtime validation did complete a diagnostics-off 3804x1932
single pass and a separate five-frame repeat, with exact image comparisons and
clean external Windows monitoring. See
[ProductionStabilityValidation.md](ProductionStabilityValidation.md) for the
timings, handle/private-memory samples, and remaining endurance limitations.

The DirectFloat comparison uses the existing 67x35 full-set, 256-iteration
regression fixture. A preliminary translated coarse DirectFloat grid at 6912
iterations failed its baseline MPFR sample check before the production toggle
was tested, so it was not used as a trusted comparison. That disagreement was
not diagnosed as part of this diagnostic-policy change; these tests do not
claim universal FP64 agreement at arbitrary translated boundary samples.
