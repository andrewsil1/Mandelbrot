# Progressive viewport presentation

Click zoom immediately scales the current visible bitmap around the selected
point, placing that point at the new viewport center. Zooming out fills newly
exposed space with the interior background color. Repeated clicks transform the
current composite, including any pixels already published by the active render.

The renderer publishes direct GPU batches, completed perturbation slice
readbacks, sparse double-double corrections, and individual MPFR repairs. Pending
and glitched results leave the preview intact. Interim colors retain the histogram
palette of the last displayed completed image, including across repeated zooms
and changes in iteration budget. Counts above that palette's budget use its final
bin. Only the initial render uses a logarithmic fallback. The histogram is replaced
when a completed image is displayed after all render and repair passes finish;
superseded renders cannot replace it. Existing repair-budget limits still apply.

A bounded mailbox coalesces updates for WPF presentation every 33 ms, updating
the affected row range. It never queues a dispatcher operation per pixel or GPU
submission. View and bitmap identity checks reject both partial and final output
from superseded renders. GPU jobs still finish serially, with queued requests
coalesced to the newest view. Perturbation updates follow the existing readback
cadence rather than adding a readback after every compute submission.

Presentation writes resolved pixels directly into the locked BGRA32 WPF back
buffer; it does not maintain a second full-frame display array. Zoom preview
also copies its rendered bitmap directly into that back buffer. Final coloring
replaces the completed iteration array only after validation and all publication
callbacks have finished, transferring ownership of that array to the result.

Queued renders run through an iterative pump. Each frame owns a cloned MPFR
viewport snapshot, while the UI disposes replaced views and retired history
entries on resize, Reset, Back, and close. Identity checks still use the source
viewport object, so a stale frame cannot update the current image. Closing the
window retires UI-owned coordinates immediately; an active worker releases its
own snapshot when its existing work finishes.

`ProgressiveChecks` exercises preview preservation, sparse indexing, concurrent
corrections, unchanged-update suppression, actual WPF row writes and off-center
zoom-out scaling, plus progressive/final image equivalence on direct and deep
GPU fixtures. These are automated tests, not a live user-interaction or 4K
presentation-latency measurement.

Run the full configuration-matched renderer suite:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
```
