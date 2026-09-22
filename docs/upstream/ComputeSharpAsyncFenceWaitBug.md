### Description

ComputeSharp's asynchronous fence-completion path registers a native wait but
does not unregister it before closing the waited-on event and freeing the
callback context. This appears to leak native wait resources and violates the
Win32 wait-lifetime contract.

Confirmed in released ComputeSharp 3.2.0 (tag commit
`9a7c9e0c755bf68447f7293e5729547750fe6be3`) and the current main source inspected
at `40dbe40de0b1eafb570e2af43f78bcdbffdcc00a`:

- [v3.2.0 execution implementation](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)
- [Inspected main implementation](https://github.com/Sergio0694/ComputeSharp/blob/40dbe40de0b1eafb570e2af43f78bcdbffdcc00a/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)

`WaitForFenceAsync` calls `RegisterWaitForSingleObject` with `dwFlags: 0`, keeps
the returned wait handle only in a local variable, and returns the task source.
`WaitForSingleObjectCallbackForWaitForFenceAsync` closes the event, frees both
GCHandles/context, and returns the command list/allocator to the pool, without
calling `UnregisterWait` or `UnregisterWaitEx`. Inspection of the installed
3.2.0 assembly's IL confirmed the same missing cancellation calls.

### Reproduction Steps

1. Use ComputeSharp 3.2.0 with a long-running sequence of bounded compute
   submissions. Reuse the device and GPU buffers; do not allocate resources per
   submission except the contexts required by the public API.
2. Submit contexts with `DisposeAsync()` and await every completion. Our workload
   permits at most two in-flight contexts on a single compute queue, drains
   before buffer readback/reuse, and uses UAV barriers between dependent slices.
3. Sample the process's native `HandleCount` throughout the run and repeat the
   frame on the same device. Work must take long enough that the fence has not
   already completed when `ExecuteCommandListAsync` checks it; otherwise the
   synchronous fast path does not exercise this code.
4. Observe continuing handle growth despite all awaiters completing. Compare
   with a runtime that retains and explicitly unregisters the native wait.

This is the public-API submission shape, not a standalone independently tested
reproducer:

```csharp
ComputeContext context = device.CreateComputeContext();
context.For(pixelCount, shader);
context.Barrier(buffer);
await context.DisposeAsync();
```

Our observed workload is a sliced Mandelbrot perturbation renderer, 2880x1460,
6912 total iterations, maximum 128 scalar iterations per dispatch, delayed
readback every four slices, and at most two in-flight contexts. Both FP64 and
double-double work occurs. An instrumented frame followed by a diagnostics-off
frame produced exact matching images with no unresolved pixels or new relevant
Windows error events; image correctness did not expose the wait leak.

### Expected Behavior

Completed asynchronous submissions release their wait registrations, events,
unmanaged callback contexts, and GCHandles. Native handles should remain bounded
after warm-up rather than grow with completed submissions.

### Actual Behavior

In the unmodified 3.2.0 run, externally sampled handles rose from 13,354 near the
end of the instrumented frame to 26,093 near the end of the following quiet
frame, a further 12,739 handles. The process started with 30 handles before
initialization. These are sampled process-wide counts, not an attribution of
each handle to a particular internal wait.

The inspected stock runtime assembly SHA256 is
`C06FB300AF75CB12E41875CA00EA176F1CA1BE0B697B741F6C9606A3F2D65240`.

### System Info

- ComputeSharp / ComputeSharp.Core / released generators: 3.2.0
- Windows 11, build 26200, x64
- NVIDIA GeForce RTX 5080; driver 32.0.16.1074 (as recorded by Windows CIM)
- Intel Core i9-14900K
- .NET 8 application; .NET SDK 10.0.401
- Release, with application instrumentation explicitly disabled for the second
  frame

### Additional Context

[RegisterWaitForSingleObject documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerwaitforsingleobject)
requires explicit unregistration, including for WT_EXECUTEONLYONCE waits, and
states that closing the handle while a wait remains pending is undefined.
[UnregisterWaitEx documentation](https://learn.microsoft.com/en-us/windows/win32/api/threadpoollegacyapiset/nf-threadpoollegacyapiset-unregisterwaitex)
also warns against blocking unregistration from the callback itself.

We have a pinned local runtime source patch with these properties:

- WT_EXECUTEONLYONCE registration, retained wait handle.
- Atomic publisher/callback handshake: the callback can run before registration
  returns, so adding a handle field without coordinating publication is unsafe.
- Normal callback cleanup uses nonblocking UnregisterWaitEx with null completion
  handle; ERROR_IO_PENDING is accepted only on this callback path.
- If the callback arrived first, the publisher cleans up outside the callback
  using blocking UnregisterWaitEx with INVALID_HANDLE_VALUE, joining the callback
  before freeing its context.
- Event/context/GCHandles are released only after accepted cancellation.
  Unexpected cancellation failures fault the awaiter and quarantine resources
  rather than free objects whose native ownership is uncertain.
- Wait setup failures synchronously drain already-submitted work before resource
  recycling. Broader device-loss behavior is not redefined by this patch.
- Source-generated, last-error-aware Win32 bindings support the runtime's
  DisableRuntimeMarshalling configuration.

The publication mechanism is small and does not block the callback waiting for
registration to return:

```csharp
internal struct FenceWaitPublication
{
    private int state;
    public bool OnRegistered() => Arrive(1, 2);
    public bool OnSignaled() => Arrive(2, 1);

    private bool Arrive(int own, int other)
    {
        int previous = Interlocked.CompareExchange(ref state, own, 0);
        if (previous == 0) return false;
        if (previous == other &&
            Interlocked.CompareExchange(ref state, 3, other) == other) return true;
        throw new InvalidOperationException();
    }
}
```

The publisher writes the wait handle before OnRegistered. The callback calls
OnSignaled. The party receiving true is the sole cleanup owner; the party
receiving false does not access the context again. A one-shot wait is essential.

The final local patch passed full Release and Debug renderer suites, including
dispatch seams, buffer reuse, queue ordering, and raw FP64/double-double
comparisons against 768-bit MPFR. Added lifetime checks passed in both builds:

| Check | Release | Debug |
| --- | --- | --- |
| Publication races | 10,000; exactly one cleanup owner | Same |
| Real native waits, both callback orderings forced | 2,000; handles 269 to 269 | 2,000; handles 276 to 276 |
| Twenty quiet renderer frames | Exact images, zero unresolved; handles 609 to 609 | Exact images, zero unresolved; handles 618 to 618 |
| Native last-error capture | ERROR_INVALID_HANDLE (6) | Same |

Tests do not inject device removal, allocation failure, or invalid native
thread-pool wait handles.

Repeating the externally monitored 2880x1460 instrumented-plus-quiet workload
with the patched runtime passed: exact whole-image/status comparisons, 64
768-bit MPFR samples with zero mismatches/unresolved, no new relevant Windows
events or changed dumps, and zero process/monitor exit codes. Post-initialization
handle samples stayed between 508 and 1,009 and finished at 819, rather than
progressively climbing into tens of thousands. Quiet rendering took 27.812
seconds, essentially unchanged from the unmodified 27.813-second quiet frame;
these baseline-first runs are not controlled performance benchmarks.

We previously experienced a GPU scheduling bugcheck in this application, but
there is no established causal link to this wait defect. This report concerns
the concrete native lifetime violation and observed resource accumulation,
not a claim that this patch resolves that historical kernel failure.
