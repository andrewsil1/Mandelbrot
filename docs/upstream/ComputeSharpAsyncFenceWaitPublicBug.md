### Description

The asynchronous fence-completion implementation registers a native wait but
does not unregister it before closing the waited-on event and freeing the
callback context. This appears to violate the Win32 registered-wait lifetime
contract.

This report is based only on the following publicly available upstream source:

- [ComputeSharp v3.2.0 GraphicsDevice.Execute.cs](https://github.com/Sergio0694/ComputeSharp/blob/v3.2.0/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)
- [Main source at 40dbe40de0b1eafb570e2af43f78bcdbffdcc00a](https://github.com/Sergio0694/ComputeSharp/blob/40dbe40de0b1eafb570e2af43f78bcdbffdcc00a/src/ComputeSharp/Graphics/GraphicsDevice.Execute.cs)

### Reproduction Steps

This is a source-level API-lifetime finding, with inspection steps rather than
a standalone runtime reproducer:

1. Open GraphicsDevice.Execute.cs in either linked revision.
2. Inspect WaitForFenceAsync. It calls RegisterWaitForSingleObject with flags
   zero and stores the returned registration handle only in a local variable.
3. Inspect CallbackContext and WaitForSingleObjectCallbackForWaitForFenceAsync.
   The registration handle is not retained in the context. The callback closes
   EventHandle and frees its context/GCHandles without calling UnregisterWait
   or UnregisterWaitEx.
4. Compare that lifecycle with the Microsoft documentation linked below.

### Expected Behavior

The registered wait should be explicitly unregistered before releasing objects
whose lifetime depends on it. Cleanup must also handle a callback that races
the return from registration, and avoid blocking unregistration from inside
the callback itself.

### Actual Behavior

The registration handle is discarded. No explicit native wait cancellation is
performed in the inspected completion path before the event/context are freed.

Microsoft's RegisterWaitForSingleObject documentation requires explicit wait
unregistration, including for WT_EXECUTEONLYONCE waits. It also states that
closing a handle while its wait is still pending is undefined behavior.
Consequently, changing the registration to one-shot by itself would not satisfy
the documented cleanup contract.

### System Info

- Affected released source: ComputeSharp v3.2.0, commit
  9a7c9e0c755bf68447f7293e5729547750fe6be3.
- Same implementation present in inspected main revision
  40dbe40de0b1eafb570e2af43f78bcdbffdcc00a.
- Finding concerns Win32 wait ownership rather than GPU-specific shader behavior.

### Additional Context

- [RegisterWaitForSingleObject](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-registerwaitforsingleobject)
- [UnregisterWaitEx](https://learn.microsoft.com/en-us/windows/win32/api/threadpoollegacyapiset/nf-threadpoollegacyapiset-unregisterwaitex)

The latter documents nonblocking cancellation, ERROR_IO_PENDING when callbacks
are still outstanding, and the deadlock risk of blocking cancellation from the
callback itself. A fix should retain the registration handle, coordinate its
publication with completion, cancel the wait explicitly, and release its event
and context only once native lifetime requirements have been satisfied.

This report does not attribute any particular GPU failure to this defect.
