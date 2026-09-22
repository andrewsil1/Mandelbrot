# GPU Trace Profiling

The following records the earlier ETW-only investigation. GPU execution timing
has since been captured with Nsight Systems, and bounded asynchronous submission
has been implemented. See [QueueConcurrencyProfiling.md](QueueConcurrencyProfiling.md)
for the current measurements, policy, and reproduction commands.

## Capture And Validation

On 2026-09-16, one existing Release scaling executable was profiled at
1920x980 on the NVIDIA GeForce RTX 5080. It used the existing failure-view
fixture, 6912 iterations, 128-iteration slices, eight-slice journal groups,
and readback interval 4. No renderer, queue policy, workload limit, or Windows
watchdog setting changed. WPR GPU and CPU profiles recorded system-wide ETW
activity. The capture/test process was elevated because WPR requires Windows
profiling privileges; this is a profiling-context difference from ordinary
non-elevated runs.

The stage passed: two references, 2749 repaired pixels, zero unresolved pixels,
zero mismatches among 64 deterministic 768-bit MPFR samples, and matching durable
intents/completions for all 5670 dispatches and 735 groups. The full Release
regression suite was then rebuilt/run and passed, including 9657 matched
submissions and 3633 bounded groups. Legacy native-package NU1701 warnings remain.

Trace headers report zero lost buffers/events. WPR stopped successfully.
The original capture wrapper incorrectly reported failure because it obtained
a null child exit code with Start-Process/WaitForExit under Windows PowerShell.
Do not rewrite that raw metadata as a clean helper pass. The stage's own durable
completed/passed record and stdout establish numerical/journal success instead.
The wrapper now uses Start-Process -Wait -PassThru and rejects unavailable exit
status. GPU-free checks cover both successful and failed child exit statuses.
No automatic GPU retry was performed to fix this bookkeeping issue.

## Measurements

| Measurement | Time |
| --- | ---: |
| Standalone stage | 17.575 s |
| Renderer | 17.314 s |
| Host dispatch/submission/completion timer | 11.435 s |
| Host FP64 / DD dispatch timers | 2.939 / 8.496 s |
| Durable journal | 3.430 s |
| Journal serialization / file operations | 0.063 / 3.366 s |
| Readback | 0.239 s |
| CPU MPFR repair | 1.600 s |
| MPFR validation | 0.426 s |
| Longest host dispatch | 12.107 ms |

The DXGKRNL dump contains hardware-queue submission and completion-report
events. Context creation and hardware-queue creation associate queue handles
with RendererChecks.exe PID 20104. Attribution does not use the reporting
event's PID: completion often executes on an unrelated process's current
thread. Pairing uses hardware-queue handle plus progress-fence value.

Two node-0 streams each contain 5671 matched packets (the dispatch count plus
one initialization packet). Their intervals overlap and MUST NOT be summed:

| Stream suffix | Endpoint-latency sum | Median | P95 | Maximum |
| --- | ---: | ---: | ---: | ---: |
| 72210470 | 10.835 s | 1.203 ms | 7.368 ms | 11.990 ms |
| 72211b20 | 10.552 s | 1.189 ms | 7.209 ms | 11.978 ms |

An active node-4 stream has 1584 matched packets totaling 83.772 ms, with
0.049 ms median, 0.074 ms P95, and 0.177 ms maximum. Another has four packets
totaling 1.965 ms. The analyzer found zero duplicate submissions, unmatched
completions, pending packets, or failed submission statuses for all attributed
streams. Node numbers alone are not sufficient to assert engine types.

These are **submission-to-completion-report endpoint latencies**, not hardware
shader timestamps. They include queueing, execution, dependencies, and reporting
delay. The roughly 10.84 s packet sum versus 11.44 s host dispatch total is
consistent with most dispatch time occurring after driver submission, not CPU
command recording alone. It does not establish exactly how much is shader
execution versus scheduling/dependency overhead. The additional initialization
packet and different measurement boundaries also prevent treating their
difference as an exact CPU-overhead measurement.

Journal time is approximately 19.8% of renderer wall time in this run, largely
file operations. That is independently measured overhead between work bursts.
It is NOT a measured GPU utilization percentage. No utilization cap, sleep,
or deliberate duty-cycle limiter was introduced; short synchronous submissions
and durable checkpoints remain intentional safety constraints.

## What This Trace Does Not Resolve

The bundled legacy GPU Utilization summary preset exported only unattributed
memory-transfer data, not the compute packets above. WPA also emitted duplicate
TraceLogging activity diagnostics and missing-data errors for unrelated HTML
tables; the whole legacy-profile export reported an error despite exit code 0.
Its GPU summary and aggregate CPU wait totals must not be used as shader timing
or utilization evidence. The raw xperf packet dump is analyzed separately.

True per-shader GPU time remains unmeasured. A PIX timing capture with supported
GPU timing data, or timestamp queries inserted into the renderer's actual
command lists, is needed before deciding whether to change the submission
architecture. Do not sum overlapping queue streams or substitute endpoint
latency for GPU busy time. Microsoft describes PIX's timing capture capabilities:
[PIX timing captures](https://learn.microsoft.com/en-us/windows/win32/direct3dtools/pix/articles/timing-captures/pix-timing-captures).

One VidSchMarkDeviceAsError event with Reason 14 occurred at trace time
18.971681 s, after numerical completion and immediately before context/device
teardown around 18.998 s. Its reason has not been authoritatively decoded.
The timing is compatible with teardown, but that is an inference, not proof
that it is benign. No matching System warning/error/critical events or
Application 1000/1001 events were found since capture start. This successful
run does not prove the original 0x119 failure is fixed or establish repeat-run
driver stability. No larger workload was attempted during profiling.

## Reproduction

First run the mandatory full Release regressions from the project root:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

Then, from an elevated PowerShell session in that root:

```powershell
.\tools\ProfileRenderer.ps1
```

The script refuses active/unknown WPR sessions, captures GPU/CPU profiles,
runs exactly one 1920 stage, and stops/saves only its own recording in finally.
It does not build, retry, or advance stages. Failures require manual review.
System-wide traces may contain activity from other applications and should
be treated as potentially sensitive local diagnostic artifacts.

Dump and analyze the saved trace (substitute its directory and renderer PID):

```powershell
$wpt = 'C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit'
& "$wpt\xperf.exe" -i "$capture\renderer.etl" -o "$capture\dxg-events.csv" -a dumper -provider '{802ec45a-1e99-4b83-9920-87c98277ba9d}' -add_fieldnames
& "$wpt\xperf.exe" -i "$capture\renderer.etl" -o "$capture\trace-health.txt" -a tracestats
pwsh -NoProfile -File tools/AnalyzeRendererTrace.ps1 -CsvPath "$capture\dxg-events.csv" -RendererPid $rendererPid -OutputPath "$capture\packet-analysis.json"
pwsh -NoProfile -File tools/tests/TestProfiling.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/TestCaptureProcess.ps1
```

The analyzer requires PowerShell 7. Its CSV parser is structured and rejects
malformed supported events or timestamp inversions. GPU-free fixtures check
ownership, foreign queues, quoted fields, pairing, duplicate/failed/orphan/
pending packets, and microsecond-to-millisecond conversion. Counts must be
reviewed; unmatched/pending records are not interpreted as successful work.

## Local Artifacts

Capture directory: `tests/RendererChecks/bin/profiles/capture-20260916`.
Contains renderer.etl (approximately 1.63 GB), capture.log, original capture.json,
stdout/stderr, raw DXGKRNL CSV, trace health/statistics, CPU module samples,
packet-analysis.json, and the partial legacy WPA exports.

Stage result: `stage-1920-20260916-235501-20104.jsonl` under Release's
scaling-results directory. Journal: `dispatch-20104-20260916-235501.jsonl`.
Artifacts are excluded by the existing bin/ ignore rule. No application source
was modified for this profiling increment.
