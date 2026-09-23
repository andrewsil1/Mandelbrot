# Reusable MPFR repair temporaries

The CPU repair loop now allocates eight MPFR values once per pixel and reuses
them throughout the orbit. Previously it allocated two initial orbit values
and up to eight new values per iteration. Scratch storage is local to each
call and disposed on both escape and iteration-limit exits; nothing is shared
between parallel repair workers.

The change preserves the operation sequence, per-operation rounding to nearest
(ties to even), requested precision, escape comparison, and iteration counts.
The old orbit components are overwritten only after their cross product has
been calculated. There is no change to coordinate construction, repair budget,
parallel scheduling, progressive publication, or GPU arithmetic. Reuse across
different pixels and a native-loop rewrite were not included in this experiment.

## Measurement

Measured in Release on 2026-09-23, with an unmodified production baseline
recorded before editing `MpfrMandelbrot.cs`. The baseline source and hashes are
in [mpfr-temporaries](mpfr-temporaries). Existing unrelated workspace changes
were retained in both builds.

The fixture is 512x260, budget 6912, centered on the exact binary64 values
`-0.46729724795644717 + 0.5881255899137422i`, with vertical span `4 * 2^-40`.
The JSONL records contain exact binary64 expansions of the MPFR coordinates,
all 27 repair pixel indices and their classifications, assembly/source hashes,
and per-run timings. These are the rounded-coordinate suspended-transition
fixture, not a reconstruction of an earlier exact live-click viewport.

| Measurement | Allocating baseline | Reused temporaries | Change |
| --- | ---: | ---: | ---: |
| Serial repair kernel, 8 repetitions of 27 pixels | 1102.28 ms | 483.17 ms | 2.28x faster |
| Managed allocation for that kernel workload | 294,687,016 bytes | 55,336 bytes | 99.98% lower |
| Parallel repair phase in full rendering | 21.19 ms | 11.42 ms | 46.1% lower |
| Full frame | 295.54 ms | 284.17 ms | 3.8% lower |

Kernel measurements are medians of six runs per implementation, with warmup
and alternating AB/BA order in the same process. The allocating implementation
is a frozen copy of the original loop in the regression project. Kernel timing
excludes coordinate construction and GPU work; managed allocation does not
measure native MPFR/GMP allocation or process peak memory.

Full-frame measurements are medians of five runs after a warmup in each build,
using diagnostics-off timing attribution. The before and after processes were
run sequentially, without balanced cross-build ordering or GPU clock control.
The full-frame improvement is preliminary; it is not a 4K performance claim.
Repair work remained exactly 143,899 loop evaluations per frame, with 27 repairs,
two reference passes, 6,352 initial FP64 glitches, and no unresolved pixels.

## Correctness and reproduction

The before/after repair indices, all repair classifications, and complete
frame image hashes match. All captured repair points agree with the original
loop at 768 bits. A focused check covers 280 pixels around cancellation-heavy
`c=i` boundaries at 1E-28 and 1E-60, the -2 tip, a seahorse filament, the
transition fixture, cardioid/bulb boundaries, and immediate escapes. It checks
the old and new loops at both 384 and 768 bits, cross-checks the 384-bit result
against the original 768-bit result, and covers zero/negative/tiny budgets.
The focused check is also part of the normal renderer regression suite.

The full Release and Debug renderer regression suites passed after the change.
Both completed 17,871 journaled submissions and 5,369 bounded groups, including
repair budgets, sparse dispatch/recovery, 768-bit MPFR comparisons, progressive
publication, production rendering, and fence lifetime stress. Logs and their
hashes are saved in `mpfr-temporaries/regression-*.log` and `validation.json`.
No new live-window interaction or large-resolution timing run was performed.

Run a new profile from the repository root, using a new output filename:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore -- --mpfr-temporaries docs/mpfr-temporaries/new-run.jsonl
```

Raw evidence: [before.jsonl](mpfr-temporaries/before.jsonl),
[after.jsonl](mpfr-temporaries/after.jsonl).
