# GPU Execution Profiling And Queue Concurrency

Ordinary Release builds now disable diagnostic collection and logging by
default; the timings below describe explicitly instrumented runs. Debug defaults
to diagnostics enabled. See [ReleaseDiagnostics.md](ReleaseDiagnostics.md) for
the defaults, production-path validation, and preserved safety checks.

## Measurement Method

Nsight Systems 2026.1.3 is installed locally and successfully captures the
renderer's Direct3D 12 GPU workloads. Unlike the earlier ETW progress-fence
analysis, these measurements use GPU workload start/end timestamps. Each
compute command list contains one dispatch and its UAV barriers; the measured
interval is command-list GPU execution, not a count of shader arithmetic alone.

Captures are application-scoped, with individual DX12 workloads and wait-call
tracing enabled. CPU sampling/context-switch tracing and hardware-metric
sampling are disabled. No elevation or Windows watchdog modifications were
needed. Nsight instrumentation remains a measurement overhead; ordinary runs
are therefore measured separately.

The analyzer requires exact agreement between GPU workload count, renderer
dispatch count, and journal begins. Mode attribution requires one compute
queue, nonoverlapping GPU intervals, and ordered ExecuteCommandLists API
correlations. It rejects missing, ambiguous, reordered, and inverted data.
Ordinal correspondence then identifies the FP64 and DD portions. Compute
window coverage means captured execution time divided by the first-to-last
compute interval. It is not Task Manager utilization, occupancy, or utilization
across all GPU engines. Copy time is reported separately, not added to coverage.

## 1920x980 Comparison

RTX 5080; original device-loss view, scale 2^-40, 6912 iterations, 128-iteration
slices, journal groups up to eight, readback interval four, metrics buffer
disabled, acceleration requested BLA (this fixture falls back to rebasing).

| Profiled measurement | Synchronous | Two in-flight |
| --- | ---: | ---: |
| Renderer wall time | 16.687 s | 16.236 s |
| Exclusive host dispatch/recording/wait | 11.209 s | 10.809 s |
| GPU compute execution | 10.141 s | 10.167 s |
| GPU FP64 / DD execution | 2.343 / 7.798 s | 2.346 / 7.820 s |
| Maximum GPU compute workload | 11.913 ms | 11.956 ms |
| Compute interval gap time | 4.408 s | 3.873 s |
| Compute interval coverage | 69.70% | 72.41% |
| GPU copy execution | 3.956 ms | 4.135 ms |
| Durable journal | 3.146 s | 3.030 s |

Both captures contain 5670 compute and 1584 copy workloads. Both repaired 2749
pixels, finished with zero unresolved pixels, and agreed with all 64 sampled
768-bit MPFR checks. The profiled wall-time reduction is 2.7%; GPU execution
time is essentially unchanged, so this is a host/queue-gap improvement rather
than faster orbit arithmetic.

Four additional uninstrumented fresh processes ran in order 1,2,2,1:

| Queue setting | Renderer times | Mean host dispatch/wait |
| --- | ---: | ---: |
| 1 | 16.408 / 16.530 s | 11.069 s |
| 2 | 16.300 / 16.295 s | 10.750 s |

This small sample shows approximately 1.0% lower mean renderer time and 2.9%
lower mean dispatch/wait time. It is not a statistical guarantee for other
locations or GPUs. Durable logging and MPFR repair remain substantial costs;
logging variability can exceed the concurrency gain.

## Larger Resolution Validation

The two-submission 2880x1460 capture passed: renderer 36.313 s, measured GPU
compute 22.819 s, maximum GPU workload 12.458 ms, 12636 compute dispatches,
6105 repaired pixels, zero unresolved, zero mismatches in 64 MPFR samples.
This is a validation run, not a paired speedup claim. Durable journal time was
7.657 s, illustrating the variability that obscures small concurrency gains.

The original near-4K fixture was then profiled in separate fresh processes:

| 3804x1932 measurement | Synchronous | Two in-flight |
| --- | ---: | ---: |
| Renderer wall time | 63.459 s | 61.568 s |
| Exclusive host dispatch/recording/wait | 43.067 s | 41.415 s |
| GPU compute execution | 40.175 s | 40.199 s |
| GPU FP64 / DD execution | 9.088 / 31.087 s | 9.107 / 31.092 s |
| Maximum GPU compute workload | 14.329 ms | 14.327 ms |
| Compute interval gap time | 16.419 s | 14.496 s |
| Compute interval coverage | 70.99% | 73.50% |
| Maximum submitted-but-not-GPU-complete workloads | 1 | 2 |
| GPU copy execution | 14.406 ms | 14.735 ms |
| Durable journal | 12.653 s | 12.404 s |

Both cases have 22032 compute dispatches, 6129 GPU copy workloads, 6120 slice
readbacks, and 2856 journal groups. Both repaired 10570 pixels, with zero
unresolved and zero sampled MPFR mismatches. The paired profiled renderer time
is 3.0% lower and host dispatch/wait time is 3.8% lower. This is one profiled
pair, not a statistical or cross-fixture performance guarantee. The GPU
execution time is unchanged; the captured queue bound is actually observed,
not inferred solely from the managed task counter.

Capture prefixes are `nsight-2880-depth2-20260916-172113-27864`,
`nsight-3804-depth1-20260916-172215-43420`, and
`nsight-3804-depth2-20260916-172419-28376`. Renderer PIDs are 34340, 38476,
and 17868 respectively. Prefix timestamps are local; durable stage timestamps
are UTC (2026-09-17).

## Submission And Lifetime Policy

`MANDELBROT_INFLIGHT` accepts only 1 or 2; default 2. Setting 1 restores the
synchronous comparison path. DirectFloat64 remains synchronous. Pixel limits,
128-iteration slicing, readback cadence, eight-submission journal limits, and
the one-second diagnostic brake are unchanged. There are no sleeps or a
GPU-utilization target.

The render thread records/submits each ComputeContext and consumes its
`DisposeAsync()` ValueTask exactly once via a Task. ComputeSharp submits
immediately, signals its compute fence, and retains the detached command list
and allocator until the asynchronous completion callback returns them to the
pool. The renderer holds at most two unconsumed submissions and waits for the
oldest before submitting another. It does not use Task.Run for GPU submission.

All slices share the existing compute queue. UAV barriers order classification,
resume-state, and metrics writes before subsequent slices. Before copy-queue
readback, durable checkpoints, batch initialization, reference changes, or
resource disposal, the renderer drains all issued work. No dependent slices
execute on separate compute queues.

ComputeSharp 3.2.0's asynchronous fence callback does not check device removal.
A removal-signaled fence must not be recorded as a successful dispatch. The
renderer obtains an AddRef'd ID3D12Device through the public InteropServices
export, calls the SDK's GetDeviceRemovedReason after consuming each completion,
and releases that interface after pending tasks are consumed. This uses the
supported COM ABI, not reflection into private library command lists.

On failure, no new GPU work or retry is authorized. Already-issued tasks are
consumed before releasing their buffers, without falsely logging tail success;
cleanup preserves the original exception. The duration brake measures
submission-to-observed-completion age, which can overlap between submissions.
Exclusive host timing instead sums recording/submission and actual host wait
intervals, never overlapping ages. The brake cannot cancel already-issued work
or preempt a stuck driver, and the window is not a watchdog-safety guarantee.

The subsequent [submission timing correction](SubmissionSafetyTiming.md)
starts the safety clock after shader/pipeline recording and captures it on the
completion continuation, rather than late FIFO consumption. The earlier
implementation also included these host delays; its maxima should not be
treated as hardware GPU durations or compared directly with corrected maxima.

Inspected pinned library sources:
[ComputeContext](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Shaders/ComputeContext.cs),
[device execution](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs).
Profiling documentation:
[NVIDIA Nsight Systems](https://docs.nvidia.com/nsight-systems/UserGuide/).

## Reproduction And Regression

Final full Release and Debug suites both passed after enabling the default
window and adding pending-checkpoint/FIFO checks. Each validated 16198 logged
submissions across 5144 bounded groups. The Nsight measurement fixtures, earlier
ETW packet-parser fixtures, and capture child-exit checks also passed. Existing
NU1701 legacy native-package warnings remain; no new compile errors occurred.

System critical/error/warning events and Application crash events 1000/1001
were queried from profiling start (2026-09-17 00:07 UTC) through the larger
captures; neither query found matches. This is only event-log evidence, not a
proof of driver correctness or an authoritative decoding of the earlier
VidSchMarkDeviceAsError reason.

First build/validate the Release application through its mandatory full suite:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
pwsh -NoProfile -File tools/tests/TestNsightMeasurements.ps1
```

The suite covers both submission policies, exact images/work counts for FP64
and DD, MPFR comparisons, resumed state, sparse maps, batch seams, reference
reuse, readback intervals 1/4/8, journal limits 1/8, native status/release,
pending-checkpoint rejection, FIFO backpressure, failure cleanup, and device
removal falsely signaling completion (simulated without resetting hardware).

Capture one requested stage per process, reviewing smaller results before
advancing. No build, automatic retry, or multi-stage loop is performed:

```powershell
pwsh -NoProfile -File tools/ProfileNsightRenderer.ps1 -Width 1920 -InFlight 1
pwsh -NoProfile -File tools/ProfileNsightRenderer.ps1 -Width 1920 -InFlight 2
```

Width also accepts 2880 and 3804. The installed CLI path can be overridden with
`-NsysPath`. Capture output is an ignored local `.nsys-rep`, SQLite export, and
JSON analysis under `tests/RendererChecks/bin/profiles`. A unique passing durable
stage result is required even if the profiler itself exits successfully.
Diagnostics are preserved in JSON. The captures warn that CPU scheduling,
NVTX, and OS-runtime data are incomplete; no CPU scheduling/wait attribution is
claimed from those channels.

The initial paired captures are `nsight-baseline-1920` and `nsight-async-1920`.
Their stage PIDs are 15716 and 45380. Repeat-process PIDs are 5876, 46916,
38784, and 23736. Stage timestamps and the journal provide raw evidence.

Successful correctness and timing checks do not establish that the historical
0x119 paging-command crash has been fixed, nor prove repeat-run driver stability.
