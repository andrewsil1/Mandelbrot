# ComputeSharp 3.2.0 submission audit

The current perturbation renderer permits two in-flight asynchronous command
lists on the same compute queue, draining before readback and resource reuse.
The synchronous inspection below is retained as baseline evidence. See
[QueueConcurrencyProfiling.md](QueueConcurrencyProfiling.md) for the asynchronous
lifetime audit, device-removal check, and runtime validation.

The later delayed-readback path uses an explicit single-shader ComputeContext
with UAV barriers before synchronous disposal. Readback now occurs at a bounded
cadence rather than after every slice. See
[DelayedReadbackValidation.md](DelayedReadbackValidation.md) for the current
ordering policy and validation; the inspection below records the earlier For
and per-slice-copy baseline.

This is static source inspection, not a diagnosis of the system's 0x119 crash.
The application retains ComputeSharp 3.2.0's released generators/Core, but now
builds its runtime from a pinned local source patch. The synchronous inspection
below uses the original tag, not the current main branch. See
[the dependency notes](../third_party/README.md) and
[ProductionStabilityValidation.md](ProductionStabilityValidation.md) for the
later native asynchronous wait fix and its separate validation.

## Synchronous completion

`GraphicsDeviceExtensions.For` constructs a compute context, records one shader,
then disposes the context. `ComputeContext.Dispose` executes and waits for the
command list. `GraphicsDevice.ExecuteCommandList` submits to the selected queue,
signals its fence, checks the completed value, and calls `SetEventOnCompletion`
with a null event handle if completion has not been reached. Microsoft documents
that the null-handle form blocks until the specified fence value is reached.
Only after that path completes are the command list and allocator returned to
their pool. Our next slice therefore does not overwrite the preceding slice's
state while its normal synchronous dispatch is still executing.

- [Dispatch extensions](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/Extensions/GraphicsDeviceExtensions.Dispatching.cs)
- [Compute context](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Shaders/ComputeContext.cs)
- [Device execution](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)
- [Null-handle fence wait](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12fence-seteventoncompletion)

## Native resource reuse

`ID3D12CommandListPool.Rent` dequeues an existing command-list/allocator pair,
resets it, and creates a new pair only when no pooled pair is available.
`Return` enqueues the detached pair for later reuse. Consequently, a managed
context per batch does not imply a permanently accumulating native allocator
per batch. The single-window UI serializes renderer jobs; its normal render
path is not intentionally flooding asynchronous submissions.

- [Command-list pool](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/Commands/Interop/ID3D12CommandListPool.cs)

Image, reference, BLA, and metrics resources remain alive across synchronous
slices. The batch-local state workspace remains alive for the pass (and across
DD reference retries). Intermediate readback occurs after `For` returns. New
batches and references explicitly initialize their state and classifications.
Partial workspace construction registers each successful allocation for cleanup.

The span-based structured-buffer readback implementation creates temporary
staging storage. The sliced renderer instead allocates an explicit batch-sized
`ReadBackBuffer<int>` once, copies each output prefix into that same resource,
waits synchronously for the copy queue, then reads its mapped span. DD reference
retries reuse this resource as well. This avoids new native staging allocations
for every slice; it does not eliminate the copy submission or its fence wait.

- [Structured buffer implementation](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/Resources/Abstract/StructuredBuffer%7BT%7D.cs)

## Limits of these findings

Normal completion ordering is consistent with allocator reuse. This does not
prove driver correctness, rule out a library failure path defect, or establish
the cause of the kernel paging-command failure. Slicing bounds logical orbit
iterations, including BLA skips, not wall-clock duration. Shader setup, divergence,
memory residency, synchronization, and driver scheduling remain runtime costs.
No watchdog registry settings are changed. No GPU reset is deliberately induced.
The deep 4K crash reproduction is excluded from this validation increment.
