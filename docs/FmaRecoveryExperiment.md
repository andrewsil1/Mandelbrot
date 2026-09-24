# FMA double-double recovery experiment

> Post-v1.5 cleanup: the completed experiment summarizer and promotion smoke harness were removed. Recorded results remain available.

> Promotion: FMA now lives in the production DD shader. The experimental shader,
> renderer switch, generator and `--fma-experiment` entry point have been removed.
> The results and experiment reproduction commands below are historical evidence.
> Current production validation is recorded in [FmaProduction.md](FmaProduction.md).

## Decision

Keep explicit FP64 FMA available through the internal experiment harness. The
application still uses its existing Dekker-based DD shader by default. The
first small balanced comparisons were positive enough to justify the larger
resolution experiment recorded below. Portability to other adapters remains
unverified.

The candidate replaces only product-error reconstruction in DD multiplication
and squaring with `Hlsl.FusedMultiplyAdd(a, b, -product)`. Cross-term addition
order, normalization, IEEE-strict compilation, error estimates, BLA selection,
reference selection, iteration limits, slicing, queue bounds and MPFR repair
remain the same. No ordinary multiply-add expression substitutes for FMA.

ComputeSharp.Core 3.2.0 already exposes the double-precision intrinsic; no
runtime or compiler modification was needed. Its generated HLSL contains
explicit `fma` calls. The GPU probe also preserves the residual `-2^-54` for
`(1 + 2^-27) * (1 - 2^-27)`, which an unfused `a*b-product` loses. This checks
fused behavior, not native GPU instruction count or hardware utilization.

Microsoft documents the HLSL intrinsic and its support requirements at
<https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-fma>.
Only the RTX 5080 was exercised here. The successful probe does not establish
cross-device support. Extreme arithmetic cases are semantic checks against
.NET FMA; the experiment does not claim correctly rounded DD results for all
underflow, overflow, or nonfinite inputs.

## Focused validation

`--fma-experiment` bypasses unrelated regression startup work while iterating.
Each run performs:

- One small GPU arithmetic dispatch using the candidate's actual helpers:
  2,060 operand pairs, including special values, compared with the matching
  CPU FMA expressions. 4,096 normal-range products and squares are independently
  checked against 768-bit MPFR, with a `2^-100` relative-error limit.
- Seven 17x9 raw-orbit fixtures in rebasing and BLA modes: escaping, exact -2
  tip, exact i boundary at two depths, two filament depths and a period-three
  component. Every trusted baseline/candidate count is checked against 768-bit
  MPFR; their entire raw classification arrays must agree. Reversed sparse
  indices and a partial threadgroup are included.
- Raw baseline/candidate DD recovery over every FP64 failure in each exact
  live fixture. Every changed classification would receive an MPFR check;
  deterministic samples are also checked. No differences occurred.
- Complete frame timing with exact whole-image equality, zero unresolved
  pixels, unchanged reference/repair/FP64-failure counts and zero diagnostic
  timing collection.

The raw recovery comparison covered 33,280 tip and 2,063 transition pixels at
256x130, then 133,120 tip and 8,225 transition pixels at 512x260. Each tip
received 128 raw MPFR samples and each transition 129. The small fixtures use
all-pixel MPFR; the larger images do not. Repair cannot hide the raw-DD checks.

## Initial timing results

Both modes run in the same Release executable and process on the RTX 5080.
Each fixture gets one warmup per mode, then four measured frames per mode in
balanced forward/reverse order. Medians below exclude warmups. They include
the whole pipeline, including unchanged FP64 and CPU repair costs, and use
host wall time rather than GPU timestamps. GPU clocks were not controlled and
four observations per mode do not establish a confidence interval.

| Exact live fixture | Resolution | Dekker median ms | FMA median ms | Reduction |
| --- | --- | ---: | ---: | ---: |
| Transition | 256x130 | 269.89 | 223.00 | 17.4% |
| Transition | 512x260 | 549.80 | 462.45 | 15.9% |
| Zoomed -2 tip | 256x130 | 18.32 | 17.43 | 4.9% |
| Zoomed -2 tip | 512x260 | 43.34 | 38.29 | 11.7% |

The tip measurements have a smaller absolute benefit and more timing variation.
These results establish neither an isolated DD-kernel speedup nor input-to-photon
latency. Larger resolutions are recorded separately below; no Debug,
long-duration or driver-health run was performed.

Evidence: [256-wide records](fma-experiment/run-256.jsonl) and
[512-wide records](fma-experiment/run-512.jsonl). Each contains the assembly and
generated-HLSL hashes, raw comparison results, per-frame measurements and a
terminal passed record. The adjacent `.hlsl` files preserve generated source.
The final source differs from the timed version only in host dispatch indentation;
the final regression record is documented separately below.

## Maintenance and reproduction

`ExperimentalFmaDoubleDoubleShader.cs` is a generated copy of the production
shader with two arithmetic substitutions and test-visible helper access. This
keeps the normal shader intact and avoids a per-operation runtime branch.
`ExperimentalFma` is an internal renderer property, with no application switch.
After editing the production DD shader, regenerate and inspect the candidate:

```powershell
./tools/GenerateFmaExperiment.ps1
./tools/GenerateFmaExperiment.ps1 -Check
```

Use fresh output filenames. Each command runs one bounded resolution only:
supported widths are 256, 512, 1024, 1920, 2880 and 3804. Review the completed
stage before invoking the next one; the harness never auto-advances.

```powershell
dotnet build tests/RendererChecks/RendererChecks.csproj --configuration Release --no-restore
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --fma-experiment 256 docs/repair-validation/live-20260920-181453/frames.jsonl docs/fma-experiment/new-256.jsonl
```

Experimental compilation and focused checks do not declare an application build
ready. Run the mandatory matching regression once the candidate is finalized:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

The full suite validates the default renderer; candidate-specific arithmetic
and raw-orbit coverage is supplied by the focused experiment above.

The one final full Release regression passed, validating 17,863 submissions in
5,364 bounded journal groups. Twenty quiet-render fence-stress frames retained
exact images and zero unresolved pixels; process handles went from 696 to 694.
The existing NU1701 native-package warnings remain. See the root
`fma-regression-release.log` and
[final validation hashes](fma-experiment/final-validation.json).

## Larger-resolution experiment

The follow-up changes only the harness's accepted widths. The renderer DLL
retains SHA-256 `5F9EFEEEC9C1960F0958023F757BF541F2590771F19D83F208D5FBCD70A35DAE`.
One matching full Release regression passed after rebuilding the harness:
17,863 submissions, 5,363 bounded groups, and twenty quiet stress frames with
handles 699 to 699. No full suite is repeated between resolutions. See
`fma-scaling-regression-release.log`.

The same exact live viewports, budgets, balanced timing order and focused
checks are used at each size. The 128-iteration slices, four-slice readback,
two-submission queue and BLA minimum block length four remain fixed. All timing
is complete-frame host wall time. The raw comparison includes every DD recovery
pixel; MPFR validation at large sizes remains sampled.

Run `./tools/SummarizeFmaExperiment.ps1` to verify completed reports and derive
[summary.json](fma-experiment/summary.json) from their measured frames.

All four larger stages completed successfully, each reviewed before launching
the next. Whole-frame medians, excluding one warmup per mode:

| Fixture | Resolution | Dekker median ms | FMA median ms | Reduction |
| --- | --- | ---: | ---: | ---: |
| Transition | 1024x520 | 1607.78 | 1379.00 | 14.2% |
| Transition | 1920x980 | 5122.42 | 4386.98 | 14.4% |
| Transition | 2880x1460 | 11220.48 | 9684.11 | 13.7% |
| Transition | 3804x1932 | 19425.48 | 16735.07 | 13.8% |
| Zoomed -2 tip | 1024x520 | 236.62 | 218.46 | 7.7% |
| Zoomed -2 tip | 1920x980 | 483.43 | 446.99 | 7.5% |
| Zoomed -2 tip | 2880x1460 | 955.12 | 841.27 | 11.9% |
| Zoomed -2 tip | 3804x1932 | 1552.83 | 1395.18 | 10.2% |

At the largest size, FMA saves about 2.69 seconds per transition frame. That is
about 1.16x whole-frame speedup, not a measurement of isolated DD throughput.
The baseline tip timings contain outliers; four samples per mode and uncontrolled
clocks still limit precision. No cross-device or WPF latency claim is made.

Every raw baseline/candidate classification matched at every larger stage.
At 3804x1932 this includes 7,349,328 tip pixels and 456,582 transition recovery
pixels. All complete images matched, with zero unresolved pixels and identical
reference/repair counts. The largest transition used two reference passes and
1,509 repairs; the tip used 34 passes and 185,472 repairs. The raw MPFR checks
at this size sampled 129 pixels per fixture and reported no trusted mismatch;
this is not all-pixel MPFR validation of the large images.

Reports: [1024](fma-experiment/run-1024.jsonl),
[1920](fma-experiment/run-1920.jsonl), [2880](fma-experiment/run-2880.jsonl),
[3804](fma-experiment/run-3804.jsonl). Each ends in `passed`. The application
and generated-HLSL hashes agree across these four stages. FMA remains
harness-only; these measurements do not change the production default.
