# Wider perturbation batches

The renderer now dispatches up to 131,072 FP64 pixels or 32,768 double-double
pixels per batch, four times the previous capacities. Direct FP64 rendering
is unchanged. Iteration slices remain 128 steps, readback remains every four
slices (plus first/final), and the queue remains bounded to two submissions.
The existing 128,000,000 FP64 / 16,000,000 DD iteration-work ceilings still reduce
batch sizes for long slices. The 1,000 ms observed-completion brake is unchanged.

The intent is to give each submission enough parallel work and amortize command
recording, dispatch, readback and batch turnover. Hardware occupancy was not
measured; improved occupancy is a hypothesis, not a demonstrated explanation.

## Measured results on RTX 5080

Release, diagnostics off, same process with alternating forward/reverse ordering.
Each configuration has one warmup and four measured frames. Results below are
medians. The harness checks every captured raw escape classification and every
final image pixel against the old-capacity baseline. That baseline additionally
has 64 independent 768-bit MPFR samples with zero mismatches/unresolved pixels.
This is not all-pixel MPFR proof.

| Fixture / resolution | Old batches | New batches | Time reduction |
| --- | ---: | ---: | ---: |
| Reported deep view, 1024x520 | 334.07 ms | 247.93 ms | 25.8% |
| Reported deep view, 3804x1932 | 4,251.77 ms | 2,835.10 ms | 33.3% |
| E-013 transition, 1024x520 | 1,016.20 ms | 657.39 ms | 35.3% |
| E-013 transition, 3804x1932 | 12,928.87 ms | 8,422.20 ms | 34.9% |

The screenshot-derived deep fixture uses the displayed binary64 center
`(-0.34529529051392366, 0.63220042588156822)`, scale `2^-62`, budget 10,432.
The power-of-two scale is inferred from the displayed `2.168E-019` and the
application's fourfold zoom steps. Neither the rounded center nor rounded scale
recovers the exact original live MPFR viewport at this depth. This is explicitly
a representative reconstruction; its full-resolution baseline has 1,288 distinct
escape counts, 213,882 initial FP64 glitches and 901 repaired pixels.

The E-013 fixture is the existing rounded-center suspended-transition case:
`(-0.46729724795644717, 0.5881255899137422)`, scale `2^-40`, budget 6,912.
Exact binary64 expansions of the constructed MPFR coordinates and span are
recorded in each profile's metadata. All configurations have zero unresolved
pixels and unchanged repair/initial-glitch counts and complete images.

At 3804x1932, the per-pass host loop durations are:

| Fixture | FP64 old/new | DD old/new | Submissions old/new |
| --- | ---: | ---: | ---: |
| Reported deep view | 2,516.69 / 1,704.99 ms | 1,553.12 / 949.45 ms | 3,932 / 1,048 |
| E-013 transition | 7,126.23 / 4,848.25 ms | 5,364.37 / 3,136.57 ms | 14,526 / 3,672 |

Maximum observed submission-to-completion latency among the measured new-batch
frames was 52.56 ms. This is a host measurement including queue residence, not
a GPU hardware timestamp or a guarantee about other adapters/viewports.

Larger staging/state buffers are the tradeoff: at full capacity FP64 adds
4.125 MiB of state/readback payload and DD adds 1.40625 MiB compared with the old
capacities. The two precision passes execute sequentially. Sparse DD output and
index buffers remain sized to the unresolved list from the allocation work.

## Utilization and earlier experiments

Task Manager engine utilization is not shader occupancy or a direct measure of
available FP64 throughput. Frame time and unchanged results are the optimization
criteria. A partial `nvidia-smi` sampling run is retained for the reported 3804
fixture, but it measures the entire adapter with coarse temporal windows. Its
configuration-level percentages include phase/sample-boundary effects and do
not establish per-pass occupancy or reproduce Task Manager's engine metric.

The earlier 1024x520 sweep isolated journaling overhead: 1,713.45 ms with durable
dispatch journaling versus 1,043.88 ms without it, using the same Release binary.
Longer 256/512-iteration slices slowed that workload and were not adopted.
Readback interval eight helped modestly but remains unchanged at four to keep
the scope on pixel batch width and retain the existing presentation cadence.
The earlier sweep checked image/recovery equivalence, but its baseline-selection
logic inadvertently skipped independent MPFR sampling; the batch experiment
fixes that by explicitly validating the baseline before any timed configuration.

## Evidence and reproduction

Profiles, source hashes, console logs and derived summaries are in
[gpu-throughput](gpu-throughput). Verbose dispatch journals are retained locally
under `gpu-throughput/dispatch-logs` and ignored by Git. No manual live-window
throughput measurement or Nsight occupancy trace was performed.

The internal nullable `GpuDispatchPolicy.BatchScaleOverride` is used only by the
harness to replay 1x/2x/4x capacities and is restored in `finally`. Production
leaves it unset and uses 4x. There is no application setting for the experiment.

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore -- --gpu-throughput 3804 batch:reported-deep docs/gpu-throughput/new-deep.jsonl
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore -- --gpu-throughput 3804 batch:suspended-zoom docs/gpu-throughput/new-transition.jsonl
```

Use new output filenames. Only run one GPU benchmark/test process at a time.
Regression fixtures now cross the 131,072-pixel FP64 and 32,768-pixel DD seams,
including delayed readback, reference reuse and reversed sparse BLA maps.

The finalized production default passed the full renderer regression suite in
both Release and Debug. Each run validated 15,960 submissions and 3,546 bounded
groups, including diagnostics-off rendering and fence lifetime stress. Logs are
`gpu-throughput/regression-release.log` and `gpu-throughput/regression-debug.log`;
`gpu-throughput/validation.json` records source and log hashes for this validation.

Release 1.5 sets the application version to 1.5.0. The full Release suite passed
again after the version change (15,960 submissions, 3,546 bounded groups).
Because the running application locked the normal Release executable, this build
used `-p:OutDir=C:/Users/andrewsi/OneDrive/Documents/Mandelbrot/bin/release-1.5-validation/`
and ran that directory's `RendererChecks.dll` directly. Build and regression logs
are `gpu-throughput/release-1.5-build.log` and
`gpu-throughput/release-1.5-regression.log`. The normal output executable remains
the version already running until it is closed and rebuilt.
