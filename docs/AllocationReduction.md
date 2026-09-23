# Allocation reduction: sparse recovery, presentation, references and ownership

Implemented the first four opportunities from the allocation review. Workspaces
are not pooled globally, and no cross-frame GPU resource cache was introduced.

## Changes

1. **Sparse double-double buffers:** the FP64 fallback path builds its unresolved
   list before allocating `PerturbationBuffers`, using that list's length as the
   capacity. All later retries are subsets and reuse the same buffers. Direct
   full-frame DD rendering still allocates full capacity.
2. **Full-frame arrays:** progressive display updates the WPF bitmap back buffer
   directly instead of retaining a display copy. Final coloring reuses the
   iteration array after validation and synchronous publication are finished.
   Zoom preview copies directly from its `RenderTargetBitmap` into the locked
   display bitmap, removing its managed staging array. The native preview bitmap
   and progressive iteration mailbox remain.
3. **Reference orbit MPFR temporaries:** eight local MPFR values are reused for
   recurrence and double-double conversion. No precision, rounding, recurrence
   order, escape cutoff, reference-array format or BLA policy changed.
4. **Viewport ownership:** `MandelbrotViewport` is disposable. UI replacement,
   Reset, Back, resize, validation-fixture replacement and window close dispose
   retired coordinates. Each active frame owns a separate snapshot, so disposing
   the UI's old view cannot invalidate worker/GPU recovery. Queued rendering is
   iterative, avoiding a chain of awaiting completed frame state machines.

## Measurements and limits

Baseline was recorded before production edits on 2026-09-23. Source hashes,
exact fixture coordinates, repair indices/classifications, complete image hashes
and raw samples are retained in [allocation-reduction](allocation-reduction).

| Measurement | Before | After |
| --- | ---: | ---: |
| 24 reference builds, isolated median | 124.70 ms | 59.24 ms |
| Managed allocation for those 24 builds | 33,543,784 bytes | 5,318,248 bytes |
| Reference phase, 512x260 full-frame median | 9.95 ms | 4.76 ms |
| Whole 512x260 frame median | 284.53 ms | 278.78 ms |

The isolated reference test alternates AB/BA against a frozen original loop in
the same process: one warmup pair followed by six measured pairs, at budget
6912 and the rounded-coordinate suspended-transition center. Reuse is 2.10x
faster with 84.1% lower managed allocation on this workload. Remaining allocation
includes the four output reference arrays. Native allocation and peak working
set were not measured by the managed allocation counter.

Frame profiles use the same 512x260 fixture as `MpfrTemporaryReuse.md`: budget
6912, exact binary64 center `(-0.46729724795644717, 0.5881255899137422)`, span
`4 * 2^-40`. Five measured frames follow a warmup in each process. Before and
after were separate sequential processes without balanced cross-build ordering
or GPU-clock control; the approximately 2.0% whole-frame improvement is preliminary.
Both have the exact same complete image hash, 6,352 initial FP64 glitches, two
reference passes, 27 repairs, 143,899 repair loop evaluations and zero unresolved.
These profiles are renderer-only; they do not measure WPF presentation latency.

Calculated payload reductions, **not measured working-set or VRAM residency**:

- At 3804x1932, each full `int[]` is 28.04 MiB. Removing the final color array and
  progressive display array avoids 56.07 MiB of managed array payload allocation
  per progressively displayed frame. Zoom preview avoids another 28.04 MiB
  staging allocation per click. These figures are not additive live-memory proof.
- On the measured 512x260 fixture, DD output/index capacity falls from 133,120
  pixels to 6,352: 1,064,960 to 50,816 bytes combined, saving 1,014,144 bytes of
  requested GPU buffer payload. References/BLA and other GPU buffers are separate.
- For illustration, at 3804x1932 with 456,582 unresolved pixels, those two buffers
  would shrink from 56.07 MiB to 3.48 MiB. No new large-resolution run was performed.

## Checks

During implementation, only focused checks and the bounded before/after profile
were run. `AllocationChecks` covers 72 reference cases (escaping, interior,
boundary and cancellation-heavy points, including tiny offsets and zero/one
budgets) with bitwise comparisons of all four arrays and downstream BLA data
against the original loop. It also compares in-place/out-of-place colors and
small DD capacities 1/65/137, including shrinking and reusing those buffers.

An STA dispatcher test exercises the real window's replacement, Back, Reset and
close paths, checks native MPFR disposal, and replaces or closes a viewport
while an independently owned render snapshot is active. Existing progressive
checks verify actual WPF pixel writes and zoom-preview mapping. These are
automated window tests without showing the window, not a manual UI smoke test.

After implementation was complete, the full Release and Debug renderer suites
both passed. Each completed 17,895 journaled submissions and 5,387 bounded
groups, including sparse dispatch/reuse, 4K dispatch limits, raw FP64/DD/BLA
comparisons against 768-bit MPFR, repair budgets, progressive presentation,
diagnostics-off rendering and fence lifetime stress. The configuration-matched
logs and final source/log hashes are in `allocation-reduction/regression-*.log`
and `allocation-reduction/validation.json`.

Focused reproduction from the repository root:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore -- --allocation-checks docs/allocation-reduction/new-reference-profile.jsonl
```

Use a new output filename; the profiler refuses to overwrite prior evidence.
