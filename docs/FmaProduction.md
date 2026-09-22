# Production FMA double-double arithmetic

The production DD perturbation shader uses explicit
`Hlsl.FusedMultiplyAdd(a, b, -product)` to recover the rounded high-product
residual in multiplication and squaring. Cross-term addition order,
normalization, IEEE-strict compilation, uncertainty propagation, BLA acceptance,
precision selection, reference selection and dispatch/repair limits are retained.
CPU `DoubleDouble` arithmetic is unchanged.

The experimental shader copy, internal renderer switch, generator and A/B
harness have been removed. The recorded
[balanced comparisons](FmaRecoveryExperiment.md) and their summarizer remain.
There is no FMA application setting: normal rendering uses it automatically.

ComputeSharp's existing D3D12 double-precision support check remains in place.
Microsoft documents that `DoublePrecisionFloatShaderOps` covers the extended
double instructions at
<https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_feature_data_d3d12_options>.
Performance and driver behavior were tested on the RTX 5080 only.

## Permanent regression coverage

`DoubleDoubleArithmeticChecks.Run()` executes automatically in the full suite.
One small GPU probe calls the actual production multiply, square and binary
scaling helpers. Tests verify explicit FMA in generated production HLSL and
the nonzero residual of `(1+2^-27)*(1-2^-27)`.

The probe checks 3,086 operand pairs against matching CPU FMA expressions,
covering normalized pairs, subnormals, normal exponent extremes, signed zero,
normalization ties, infinities and NaNs. It additionally compares 10,240
normal-range product, square and binary scaling results with 768-bit MPFR,
requiring relative error at most `2^-100`. Extreme-range checks establish
CPU/GPU semantic agreement, not universal correctly rounded DD arithmetic.
The old CPU reflection invocation of GPU-only shader helpers is gone.

The full suite now exercises FMA through the production path, including raw
FP64/DD MPFR comparisons, BLA, sliced state resume, sparse retries, queues,
reference reuse, complete repair and diagnostics-off rendering.

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

## Saved large-viewport smoke test

`--fma-production-smoke` reconstructs the exact final transition viewport from
the saved live session, renders with the normal production policy and checks
its complete pixel hash and fallback/reference/repair counts against the prior
passed FMA experiment. It also requires zero unresolved pixels and zero
mismatches in the 64-point 768-bit MPFR validation. Instrumentation is enabled;
its duration is not a new quiet-performance benchmark.

```powershell
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --fma-production-smoke docs/repair-validation/live-20260920-181453/frames.jsonl docs/fma-experiment/run-3804.jsonl docs/fma-experiment/production-smoke.jsonl
```

Use a fresh output filename. The command renders one transition frame only.

## Promotion results

Both full suites passed with production FMA: Release and Debug each validated
17,863 journaled submissions in 5,363 bounded groups, along with the permanent
3,086-pair GPU arithmetic probe and 10,240 MPFR comparisons. Twenty quiet-frame
fence-stress runs retained exact images and zero unresolved pixels. Handle
counts were 692 to 692 in Release and 702 to 712 in Debug; these are endpoint
observations, not proof of leak freedom. Existing NU1701 warnings remain.

The 3804x1932 production transition replay matched the saved experimental
whole-image hash exactly, with 456,582 FP64 failures, two reference passes,
1,509 repairs and zero unresolved pixels. Its 64 MPFR samples had zero
mismatches. This is a compatibility smoke test, not an all-pixel MPFR proof.

The built Release WPF app also completed its initial 992x498 transition render,
with two references, 88 repairs, zero unresolved pixels and zero mismatches in
64 MPFR samples. Windows automation read the accessibility tree and application
logs, but activation and the refreshed input attempt failed with
`failed to activate captured window`. Consequently zoom/recentering, Back and
resize were **not verified in this promotion run**. The validation window was
left open. No input-to-photon or long-duration driver-stability claim is made.

Evidence:

- [Release suite](fma-experiment/promotion-release.log)
- [Debug suite](fma-experiment/promotion-debug.log)
- [Large production replay](fma-experiment/production-smoke.jsonl)
- [WPF initial render](fma-experiment/production-ui/frames.jsonl.ui.jsonl)
- [Assembly hashes and validation status](fma-experiment/promotion-validation.json)

Application and test-copy assembly hashes match for each configuration. The
only source edits after compilation were two explanatory shader comments;
the tested arithmetic and dispatch code is unchanged.
