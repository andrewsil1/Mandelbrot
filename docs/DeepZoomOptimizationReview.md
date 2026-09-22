# Deep-zoom simplification and precision measurements

## Removed experiments

Active-pixel dispatch-map compaction and sparse pixel-rectangle BLA bounds have
been removed, including their environment settings, dedicated helper classes,
compaction profiling probe, map allocation/upload, shader indirection, and
experiment-specific tests. Historical results remain in ActivePixelCompaction.md
and SparseBlaProfiling.md, explicitly marked as historical. Sparse **retry**
dispatch is retained: it is different from compaction of survivors between slices.

Viewport-bound BLA, minimum-block selection, raw BLA profiling, sparse reference
retries, resource reuse within a frame, and the GPU lifetime/submission safeguards
remain. The obsolete sparse-bound cases in the BLA harness have been replaced
by comparisons of the retained BLA and rebasing paths. Existing slice tests
still cover sparse indices, batch seams, and buffer reuse with alternate references.

The cleanup alone passed the Release regression suite with 17,828 submissions
in 5,349 journal groups, exact quiet images, MPFR comparisons, and a 20-frame
handle check (707 to 707). This is a validation result, not a speedup claim.

## Precision comparison method

After the matching mandatory suite passes:

```powershell
./tests/RendererChecks/bin/Release/net8.0-windows/RendererChecks.exe --precision-profile 256 ./tests/RendererChecks/bin/precision-new-256.jsonl
```

Use a new report filename; reports append. The harness supports stages up to
1024 pixels wide, one requested resolution per invocation. It forces the initial
FP64 or DD mode through the same complete recovery and device-failure pipeline.
There is no shipping environment variable or adaptive tuning system for this.

Each fixture checks raw trusted counts against deterministic 768-bit MPFR samples,
compares all mutually trusted FP64/DD counts, and requires identical complete
repaired/colorized images with zero unresolved pixels before timing. Incomplete
recovery is recorded for both modes and excluded from speed comparisons; trusted
MPFR mismatches still abort immediately. An alternate reference
at the existing densest-glitch tile tests FP64 recovery potential. This is a
full-frame diagnostic pass, not a measurement of hypothetical sparse FP64 speed.

Timing uses one warmup per initial mode and four measured frames per mode in
balanced forward/reverse order. Quiet runs lock the diagnostic journal against
writes and require exact baseline images and zero diagnostic collection. Timing
includes reference construction, BLA, GPU work, readback, recovery, and coloring;
it is host wall time, not isolated GPU execution. No watchdog, dispatch, readback,
queue, or numerical acceptance tolerances are relaxed.

Rounded seahorse coordinates become largely uniform views at great depth. They
measure arithmetic/BLA cost but are insufficient evidence of arbitrary boundary
accuracy. Additional exact centers at -2 and i exercise sensitive boundary views.
The i fixtures at scales 1E-28 and 1E-60 also run automatically in the raw full
regression comparison: all 153 pixels, both precisions, and all acceleration modes.

## Correctness finding

The new i boundary fixture exposed a trusted FP64 escape-count mismatch at the
central pixel (127 versus MPFR 120 at scale 1E-28). Coordinate construction can
nearly cancel its inputs; the previous error model used the resulting small
delta and omitted the larger rounding uncertainty of those inputs.

Both shaders now bound coordinate-construction uncertainty before cancellation
and inject it on every scalar iteration. BLA applications propagate it through
their B coefficient. It is persistent uncertainty in c, not an initial error
in z. Existing trajectory/error acceptance thresholds are unchanged. These
remain engineering error estimates, not an interval-arithmetic proof for all
possible orbits. Measurements made before this correction are historical only.

## Precision decision

Automatic rendering now selects direct FP64 above the existing 1E-12 scale
boundary and FP64 perturbation below it. The former 1E-26 full-frame DD threshold
is removed. Error/glitch classification still sends only failed pixels to DD,
followed by existing additional-reference and bounded MPFR recovery. Small
diagnostic fixtures explicitly force DD for production, journal, and queue
coverage, so the new policy does not silently remove DD regression coverage.

The decision is supported by balanced, corrected-shader Release measurements on
an RTX 5080 at 512x260. Four measured frames per mode followed one warmup each.
These measurements precede the final removal of the empty repair scan; they
compare forced initial modes, so removing the selection threshold does not
change their tested numerical pipelines.

| Fixture | FP64-first mean ms | DD-first mean ms |
| --- | ---: | ---: |
| transition, 2^-40 | 328.12 | 1801.35 |
| transition-next, 2^-42 | 317.77 | 1758.89 |
| rounded filament, 1E-20 | 57.56 | 526.80 |
| rounded filament, 1E-28 | 26.29 | 174.39 |
| exact preperiodic boundary i, 1E-28 | 11.01 | 27.93 |
| exact preperiodic boundary i, 1E-60 | 12.29 | 32.06 |
| period-three component, 1E-28 | 24.57 | 176.76 |
| rounded filament, 1E-60 | 20.79 | 70.57 |

Only rows below 1E-26 measure the effect of removing the old automatic DD
threshold. Shallower rows confirm the already-existing FP64-first choice.
All timed modes agreed on the complete image and sampled MPFR results.

The -2 tip at 1E-28 is explicitly excluded: both starting modes left 1040
unresolved pixels at 512x260, just above the existing 1024-pixel repair limit.
FP64 initially flagged every pixel. This is a recovery-budget limitation, not a
successful fast render. The earlier 256x130 run completed this view but showed
that FP64-first can incur extra work on locations requiring DD everywhere.
Neither result justifies changing numerical tolerances or removing repair bounds.

An alternate FP64 reference recovered 258/6352 transition failures (4.1%) and
240/5280 next-depth failures (4.5%); it recovered none of the tip failures.
No additional FP64 retry path was added: this evidence does not justify its
dispatch/mapping complexity. Existing sparse DD recovery is retained.

Evidence: `tests/RendererChecks/bin/precision-coordinate-complete-512.jsonl`.
The earlier `precision-clean-256.jsonl` predates the coordinate-error correction;
`precision-coordinate-512.jsonl` is an aborted run, not a completed comparison.

## BLA profitability decision

Retain viewport-bound BLA with minimum DD block length four. A fresh balanced
Release raw-pass experiment on the corrected shaders used the reversed central
63x63 patch of a 1024x520 view, a 4096-iteration budget, one warmup and four
measurements per mode. All mutually trusted counts matched and deterministic
768-bit MPFR samples passed; quiet arrays exactly matched their diagnostic mode.

| Scale | Rebase ms | Minimum 2 | Minimum 4 | Minimum 8 | Minimum 16 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1E-18 | 32.00 | 32.17 | 31.94 | 31.67 | 31.66 |
| 1E-20 | 31.80 | 35.92 | 34.03 | 32.85 | 33.11 |
| 1E-28 | 31.50 | 13.55 | 13.63 | 14.90 | 17.07 |

At 1E-28 the default is about 57% faster than rebasing. At 1E-20 the
approximation remains slightly unprofitable. Minimum four retains almost all
the useful deep speedup while reducing the transition penalty relative to two.
There is insufficient evidence for a new per-frame predictor, another tree
gate, or a different shipping default. Evidence: `bla-coordinate-1024.jsonl`
under the same `tests/RendererChecks/bin` directory.

## Remaining decisions

- A zero-glitch frame now bypasses RepairGlitches entirely. The old path
  scanned the full image and allocated an empty list despite having already
  counted zero failures. Regression checks require zero repair timing when no
  pixels are repaired.
- Submission limits, queue bounds, readback cadence, barriers, lifetime repair,
  device-loss handling, and quiet Release diagnostic defaults remain intact.
- No new FP32-pair or extended-exponent arithmetic was added. FP64-first already
  provides the measured improvement; another numerical representation would
  add conversion, range, error propagation, and testing work without a measured
  requirement in these fixtures.
- Reference construction was roughly 1-4 ms for the single-reference deep
  diagnostic passes, and 10-13 ms across two transition passes. Reusing the
  center orbit during FP64-to-DD fallback could save one build, but cross-frame
  reuse also needs ownership, reference selection, iteration-budget, and BLA
  validity handling. It is left as a measured future opportunity rather than
  adding a cache in this simplification pass.
- Viewport/reference precision remains 384 bits. This work is not an arbitrary
  depth/range extension. Increasing reference precision alone would not fix
  coordinate information already lost in the viewport. Adaptive viewport and
  reference precision should be designed together if deeper ranges are needed.

No new deep-4K performance, long-duration driver stability, or universal
escape-count proof is claimed by these bounded experiments.

## Final validation

Both final mandatory commands completed successfully:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

Each suite validated 17,863 submissions. Release recorded 5,364 bounded journal
groups; Debug recorded 5,363. The 20-frame quiet-render handle checks observed
697 to 699 handles in Release and 716 to 719 in Debug, with exact images and
zero unresolved pixels in those fixtures. Debug exercised 17,251,936 BLA
searches and 1,687,360 accepted blocks in the profiling regression. Raw
boundary comparisons against 768-bit MPFR passed: the sensitive central pixel
is now flagged for repair rather than returned as a trusted wrong count.

The complete regression set includes raw FP64/DD with no acceleration,
rebasing and BLA, full recovery, sparse retries, buffer/batch seams including
4K dispatch-size coverage, both queue policies, readback intervals, journal
grouping, diagnostics-off image equivalence, and fence lifetime checks. This
does not convert the separately recorded incomplete tip benchmark into a pass.

Logs: `tests/RendererChecks/bin/final-deepzoom-release.log` and
`tests/RendererChecks/bin/final-deepzoom-debug.log`. Existing NU1701 native-package
compatibility warnings remain. `git diff --check` reported no whitespace errors.
