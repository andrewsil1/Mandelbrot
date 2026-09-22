# Double-double arithmetic specialization

> Historical pre-FMA report. Production multiplication and squaring now use
> explicit FP64 FMA, and arithmetic checks execute the production helpers on the
> GPU. See [FmaProduction.md](FmaProduction.md) for current validation.

## Scope

The GPU DD shader now uses specialized helpers for squaring and multiplication
by the binary constants 2, 1/2, and 1/4. There is no change to the CPU DD helper,
precision thresholds, error bounds, glitch handling, reference selection, BLA
policy, iteration slicing, readback cadence, or two-submission queue bound.
IEEE-strict compilation remains enabled. No FMA intrinsic is introduced.

Binary scaling acts on both components of a normalized DD pair directly, without
Dekker splitting or another normalization. This is exact when both nonzero
components and their scaled results stay normal and finite. The fast path uses
a conservative common range for all three factors: four times the smallest
normal double through 1E290. The upper bound also avoids overflow of the legacy
Dekker split. Zero components are allowed; subnormal, extreme, and nonfinite
inputs retain the existing general multiplication path.

Squaring shares one operand split and identical cross products, preserving the
general product helper's addition order. It deliberately does not replace two
cross-term additions with a reassociated expression. Compiler common-subexpression
elimination may already remove some duplicate work in the old expression; these
source changes alone do not establish a GPU instruction-count reduction.

## Validation

The mandatory full Release and Debug renderer suites passed after the final
changes. Each validated 16964 journaled submissions across 5241 bounded groups,
including raw GPU FP64/DD comparisons against 768-bit MPFR, slicing, BLA,
rebasing, sparse retries, full pipeline repair, native wait lifetimes, and
diagnostics-off production rendering. Existing NU1701 warnings remain.

`DoubleDoubleArithmeticChecks` now runs automatically in the full suite. It
invokes the actual shader helpers as managed methods on 2062 normalized or
extreme pairs, checking squares and all three scaling factors against the
original general multiplication helper. Equality allows signed-zero differences
and equivalent NaNs. Random inputs cover the normal exponent range; explicit
cases include subnormals, normalization ties, large values, infinity, and NaN.
It independently checks 1449 finite, well-scaled squares against 768-bit MPFR,
requiring relative error no greater than 2^-100. Those arithmetic-specific
tests run on the CPU; existing raw rendering regressions cover GPU translation
and execution, but do not dispatch every extreme arithmetic case individually.

Final quiet-render handle stress passed: Release 702 to 704, Debug 709 to 709
over twenty frames. Both runs retained exact images and zero unresolved pixels.

## Fixed-view timing

Existing production-stage tests measured the `suspended-zoom` fixture at scale
2^-40, budget 6912, on the RTX 5080. Both binaries used 128-iteration slices,
readback interval four, BLA requested, and two in-flight submissions. Each
process first ran an instrumented baseline with 64 MPFR samples, then five
quiet frames on the same renderer. Only quiet frames are included below.

| Resolution | Before mean (ms) | Final mean (ms) | Observed reduction |
| --- | ---: | ---: | ---: |
| 512x260 | 331.5433 | 323.8403 | 2.32% |
| 1024x520 | 1173.6728 | 1142.6342 | 2.64% |

All runs completed with zero sampled MPFR mismatches and zero unresolved pixels.
Repair counts were unchanged: 26 at 512 and 109 at 1024. Each quiet image matched
its own binary's instrumented baseline exactly, without renderer log writes.
This does not constitute a cross-binary whole-image comparison.

These are whole-frame mixed FP64/DD fallback timings, not isolated DD kernel
measurements. They ran outside Visual Studio, with no balanced before/after
process ordering or GPU-clock control. The small observed improvement is
preliminary, not a statistically established speedup or a prediction for 4K,
other viewports, or progressively deeper interactive clicks. No new 4K timing
run was performed for this increment.

Evidence is in ignored local files under `tests/RendererChecks/bin/`:

- `dd-arithmetic-before.jsonl`
- `dd-arithmetic-before-1024.jsonl`
- `dd-arithmetic-final-512.jsonl`
- `dd-arithmetic-final-1024.jsonl`

The earlier `dd-arithmetic-after*.jsonl` files describe an intermediate upper
range guard and are not the final-binary measurements above.

To build and run the matching mandatory suite:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

Release under Visual Studio does not itself enable renderer diagnostics.
`MANDELBROT_DIAGNOSTICS=1` explicitly enables them; ordinary Release defaults to
disabled. The always-on submission safety clock and completion observer remain.
