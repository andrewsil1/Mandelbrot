# Live window checks, September 20, 2026

These were the initial shallow checks. The remaining instrumented deep-zoom
checks are now complete; see [Live window validation](../LiveWindowValidation.md).

Computer Use reconnected to the existing Release Mandelbrot GPU Explorer window after the user restored its visibility. No build or source changes were made for these checks.

Results below are completed status-bar observations, not input-to-display latency measurements. Every listed frame used DirectFloat64, one reference, zero repaired pixels, and zero initial or unresolved glitches.

| Action | Render target | Displayed center | Scale | Iterations | Reported ms |
| --- | --- | --- | --- | --- | --- |
| Initial observed state | 1742x1098 | (0, 0) | 1 | 672 | 399 |
| Off-center left-click zoom | 1742x1098 | (-0.76999636539224936, 0.18730013704888093) | 0.25 | 884 | 46 |
| Maximize | 3804x1932 | (-0.76999636539224936, 0.18730013704888093) | 0.25 | 884 | 186 |
| Right-click zoom out | 3804x1932 | (-0.76896035165934806, 0.18820710776668342) | 1 | 672 | 80 |
| Back | 3804x1932 | (-0.76999636539224936, 0.18730013704888093) | 0.25 | 884 | 70 |
| Restore window | 1742x1098 | (-0.76999636539224936, 0.18730013704888093) | 0.25 | 884 | 20 |
| Reset | 1742x1098 | (0, 0) | 1 | 672 | 22 |

Back restored the previously displayed center and scale. Resizing preserved them and updated the render target. Reset returned to the full-set view and disabled Back. The app was left restored at that reset view. No error or GPU-suspension message appeared during these checks.

## Scope and limitations

- Coordinates above are rounded UI values, not exact MPFR viewport exports. Exact coordinates from the earlier renderer replay remain in `exact-viewports.json`; they are not the coordinates of this live session.
- These shallow UI checks did not exercise perturbation, DD fallback, or the repair-budget cliff. The earlier replay and regression artifacts cover those separate tests.
- The current UI/source exposes click zoom, Back, Reset, and resize, but no drag-to-pan handler. Live panning was therefore not tested.
- The existing `ui-frames.jsonl` contains an earlier instrumented frame and was not updated by the currently controlled instance. Two Mandelbrot processes were present, so process resource samples were not attributed to this window. No per-frame resource or exact-coordinate claims are made for this run.
- Maximization and restoration were tested; continuous corner-drag resizing and rapid queued input were not tested.
