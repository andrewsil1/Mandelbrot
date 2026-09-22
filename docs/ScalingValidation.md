# Controlled scaling results

Measured on 2026-09-16 using the Release renderer and NVIDIA GeForce RTX 5080.
The full Release regression suite passed before these standalone stages.
Each initial stage ran once in a fresh process; none was retried. The initial
series stopped at 1920x980. See the larger-stage follow-up below.

## Configuration

- Center reconstructed from displayed doubles: (-0.34562335012588691, 0.625450590999726).
- Scale: 2^-40; maximum iterations: 6912; slice length: 128.
- Requested acceleration: rebasing plus BLA; shaders can bypass unusable BLA tables.
- Detailed GPU work counters disabled; durable per-submission logging enabled.
- Numerical validation: 64 deterministic samples against 768-bit MPFR.
- Renderer coordinates/reference construction still use 384-bit MPFR.
- Stop conditions: device loss, a completed dispatch lasting at least one second,
  numerical mismatch, or unresolved pixels. These cannot prevent a kernel crash.

## Results

| Resolution | Total (s) | Dispatch (s) | Longest dispatch (ms) | Submissions | References | Repaired | Unresolved | Sample mismatches |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 256x130 | 1.66 | 0.258 | 8.7 | 162 | 2 | 62 | 0 | 0/64 |
| 512x260 | 3.64 | 0.917 | 11.2 | 486 | 2 | 175 | 0 | 0/64 |
| 1024x520 | 10.87 | 3.258 | 11.7 | 1674 | 2 | 778 | 0 | 0/64 |
| 1920x980 | 35.20 | 11.169 | 12.4 | 5670 | 2 | 2749 | 0 | 0/64 |

All stages ended in PerturbationDoubleDouble mode, preserving trusted FP64
output and evaluating only its flagged subset in DD. Total perturbation pixel
evaluations were 40177, 160309, 641160, and 2265607 respectively. Every journal
had matching begin/end submission IDs, no failure records, no unmatched begins,
and a maximum logical slice length of 128 iterations.

The read-only System event query found no event IDs 1001 or 4101 after 15:22
local time during these stages. Absence of those records is not a comprehensive
driver-health check.

## Interpretation

Completion and sampled numerical agreement are established for these four
specific runs. They do not prove every pixel is correct, recover undisplayed
original coordinates, establish repeat-run stability, or demonstrate that the
original 0x119 paging-command failure is fixed.

Maximum individual submission duration remained short while submission count
and total work grew. At 1920x980, measured readback was 0.619 seconds and MPFR
repair was 1.533 seconds. The named stage timers sum to approximately 13.42
seconds, leaving approximately 21.79 seconds unattributed within total render
time. That remainder includes flushed journal I/O, MPFR sample validation,
startup, and other host work. Do not attribute all of it to logging without
adding dedicated measurements. Dispatch is host-observed time including fence
completion, not a hardware GPU timestamp.

The next performance investigation should measure that host overhead separately
at an already-passing resolution before changing journal durability or readback
frequency. The larger-stage follow-up was separately authorized after these
initial results were reviewed.

## Reproduction And Artifacts

Run the normal matching-configuration regression suite first:

```powershell
dotnet run -c Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

Run one stage, then review its saved result and journal before advancing:

```powershell
dotnet run -c Release --project tests/RendererChecks/RendererChecks.csproj --no-build -- --scale-stage 256
```

Supported arguments are 256, 512, 1024, 1920, 2880, and 3804. There is no multi-stage loop
or automatic retry. Raw result files are under
`tests/RendererChecks/bin/Release/net8.0-windows/scaling-results`:

- `stage-256-20260916-222219-19564.jsonl`
- `stage-512-20260916-222245-21196.jsonl`
- `stage-1024-20260916-222316-20576.jsonl`
- `stage-1920-20260916-222427-1944.jsonl`

Each raw result contains its corresponding absolute dispatch-journal path and
unrounded measurements. Generated artifacts are excluded by the existing
`bin/` Git ignore rule.

## Larger-Stage Follow-Up

On 2026-09-16, after explicit authorization, the harness was extended with
2880x1460 and 3804x1932. Configuration checks cover both new mappings and
continue rejecting unsupported sizes. The renderer and logging implementation
were unchanged. The full Release regression suite passed again before either
larger stage was run.

Each stage ran once in a separate process, with the same configuration above.
The intermediate result, its journal, and Windows fault/recovery event queries
were reviewed before launching 3804x1932. No automatic retries were performed.

| Resolution | Total (s) | Dispatch (s) | Longest dispatch (ms) | Submissions | References | Repaired | Unresolved | Sample mismatches |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2880x1460 | 79.48 | 25.242 | 12.95 | 12636 | 2 | 6105 | 0 | 0/64 |
| 3804x1932 | 139.37 | 44.460 | 14.74 | 22032 | 2 | 10570 | 0 | 0/64 |

Both runs used the NVIDIA GeForce RTX 5080 and ended in
PerturbationDoubleDouble mode. Both journals had matching completion records
for every submission, no failed records, and a maximum logical slice length
of 128. Neither run activated the one-second completed-dispatch brake.
System and Application queries for event IDs 1001 and 4101 found no entries
from 23:10:25 UTC through the post-run review. A final broader query also
found no System warning/error/critical events and no Application events
1000/1001 during that interval. Absence of these records is not a
comprehensive driver-health assessment.

At 3804x1932, readback took 2.486 seconds, MPFR repair took 5.969 seconds,
and coloring took 0.223 seconds. Named stage timers total 53.179 seconds,
leaving 86.188 seconds unattributed within the 139.367-second total. As in the
smaller runs, this remainder includes durable journal writes, sample
validation, startup, and other host work; its individual contributors have
not yet been measured. Measuring that overhead at an already-passing size is
the next performance investigation, rather than enlarging GPU submissions
based solely on these short observed dispatch durations.

This establishes one successful nearly-4K run of the displayed-coordinate
failure case, not exact recovery of the original hidden MPFR coordinates,
all-pixel numerical agreement, repeat-run stability, or proof that the
original 0x119 paging-command failure is fixed.

Additional raw result files:

- `stage-2880-20260916-231025-4960.jsonl`
- `stage-3804-20260916-231224-34228.jsonl`

The corresponding journals are `dispatch-4960-20260916-231025.jsonl` and
`dispatch-34228-20260916-231224.jsonl` under the sibling `dispatch-logs`
directory. Result records retain the full absolute paths.

## Host Overhead Follow-Up

The subsequent instrumented 1920x980 run is documented in
[HostOverheadProfiling.md](HostOverheadProfiling.md). Durable journaling took
21.843 seconds of its 35.843-second renderer total. FP64/DD dispatch shares
of their respective slice-loop envelopes were 18.81% and 45.27%; these are
host-time ratios, not measured GPU utilization. The renderer work limits and
crash-evidence durability policy were retained, and numerical/journal checks
passed after the full Release regression suite passed again.

The subsequent grouped-journal stages also passed at 2880x1460 and 3804x1932,
taking 37.059 and 63.995 seconds respectively. Numerical, complete grouped
journal, and Windows diagnostic checks were reviewed between stages. See
[GroupedJournalValidation.md](GroupedJournalValidation.md) for the full
comparison, artifacts, and limits of these single-run validation results.
