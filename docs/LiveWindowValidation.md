# Live deep-zoom window validation

Completed September 20, 2026 (session timestamps use September 21 UTC).
The required full Release renderer regression passed with exit code 0 before
the live session. See [regression output](repair-validation/live-release-regression.log).
The build used `-p:OutputPath=bin/LiveValidation/` because an older running
process held the ordinary Release executable open. The tested executable is
`bin/LiveValidation/MandelbrotGpu.exe`; its assembly hash and process ID are
recorded in the session manifest.

## Outcome

Ten actual WPF window renders completed with zero unresolved pixels and zero
mismatches on each 64-point 768-bit MPFR sample grid. All ten exercised DD
fallback and CPU repair. This is sampled numerical validation, not proof of
every rendered pixel. The window accepted rapid native mouse input and resizing
while rendering, then settled on the exact final requested viewport.

| Workload | Render target | Renderer time | Repaired pixels | Charged repair evaluations |
| --- | --- | ---: | ---: | ---: |
| Initial transition | 992x498 | 2,540.7 ms | 88 | 490,115 |
| First of three rapid zoom clicks | 992x498 | 2,564.9 ms | 103 | 582,582 |
| Coalesced final zoom | 992x498 | 2,075.5 ms | 58 | 331,214 |
| Enlarged transition, discarded after restore | 3804x1932 | 23,463.2 ms | 987 | 5,624,519 |
| Final restored size | 992x498 | 2,081.3 ms | 58 | 331,214 |
| Continuous native sizing | 738x498 | 1,563.0 ms | 36 | 199,744 |
| Tip at scale 1e-28 | 738x498 | 329.8 ms | 2,988 | 74,266 |
| Tip after native click zoom, scale 2.5e-29 | 738x498 | 532.7 ms | 11,952 | 308,459 |
| Same zoomed tip maximized | 3804x1932 | 14,015.0 ms | 185,472 | 4,786,969 |
| Exact replay transition fixture | 3804x1932 | 29,143.1 ms | 1,509 | 8,422,274 |

The triple-click burst advanced scale from 2^-40 to 2^-46. The first completed
frame used an older viewport and was followed by the coalesced newest view.
There were no overlapping renderer frames. The maximize/restore sequence
replaced the bitmap during GPU work: the obsolete-size result was discarded,
and the final 992x498 result was applied. A native System > Size operation with
a continuous mouse drag changed the width to 738 pixels while preserving the
stored center and vertical span. Initial direct border-drag attempts had no
effect; the out-of-window enlargement attempt was rejected by the control tool.

The large transition fixture matched the previous 3804x1932 replay's exact
stored coordinates, aspect, and 6,912-iteration budget. Both produced 456,582
FP64 fallback pixels, two reference passes, 1,509 repaired pixels, and 8,422,274
charged repair evaluations. Live renderer time was 29.143 seconds versus
28.335 seconds in the replay (about 2.9% higher in this single observation).
That comparison establishes matching recorded work, not full pixel-buffer
equality or a statistically significant timing difference.

The tip used the normal UI iteration policy: 15,394 iterations at 1e-28 and
15,714 after zoom. The earlier tip replay used 4,096 iterations and different
post-click coordinates, so its latency is not directly comparable.

## Remaining performance costs

No repair-budget discontinuity left unresolved pixels in this session. Peak
charged repair work was 8.42 million evaluations, below the 120 million ceiling.
The large zoomed tip repaired 185,472 pixels despite its initial guaranteed
allowance of only 7,636 pixels, demonstrating work refunds for short orbits.

Large frames remain slow under instrumentation. The final transition spent
19.08 seconds in host-observed dispatch calls, 8.36 seconds in journaling, and
0.75 seconds in repair. The large tip used 34 references and spent 7.44 seconds
in journaling, 4.78 seconds in other host work, and 0.42 seconds in repair.
These timings include diagnostic logging and validation and are not ordinary
Release performance or input-to-photon latency. The evidence points to
dispatch and diagnostic/host overhead as larger costs than final repair in
these views; it does not isolate a new optimization's expected benefit.

## Resources and integrity

- Peak one-second sampled private memory: 652.6 MiB; working set: 522.9 MiB.
- Maximum completed host-observed dispatch: 31.5 ms.
- All 90,574 dispatch-journal records reconciled, with no pending submission or group.
- No matching adverse GPU/application event was found by the existing health checker.
- Final applied viewport and dimensions exactly matched the last request.

Process resources are tied to PID 63152. GPU samples are adapter-wide and
include other applications. Endpoint and periodic samples can miss peaks;
handle counts include startup and shutdown and do not constitute a leak test.
The instrumented window was closed normally after completion.

## Evidence and reproduction

Session directory: [live-20260920-181453](repair-validation/live-20260920-181453/).
It contains renderer records, UI requests/completions, per-process samples,
adapter-wide GPU samples, dispatch journals, manifest and health results.
[Exact frame coordinates and metrics](repair-validation/live-20260920-181453/exact-frames.json)
include lossless decimal exports of the MPFR values.
[Machine-checked summary](repair-validation/live-20260920-181453/summary.json)
records the integrity assertions.

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore -p:OutputPath=bin/LiveValidation/
./tools/StartUiValidation.ps1
# In this opt-in session only: F6 loads transition; F7 loads tip.
# Native mouse clicks, Back/Reset and native window resizing remain normal.
# Close the window when finished so the launcher writes health.json.
python tools/SummarizeUiValidation.py docs/repair-validation/live-YYYYMMDD-HHMMSS
```

The startup fixture and smaller minimum window dimensions apply only when
`MANDELBROT_UI_FIXTURE` is set and a viewport log path is supplied. Ordinary
startup behavior is unchanged. There is still no live pan handler; pan remains
covered by the existing renderer replay. The 72 replay frames across six
resolution stages were rechecked from their saved records: all passed, with
maximum repair work of 9,813,480 evaluations. This live session complements
that resolution ladder; it does not repeat all six stages in the window.
