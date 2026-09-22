# Pinned ComputeSharp runtime patch

`ComputeSharp/` contains a sparse source snapshot of upstream v3.2.0,
commit `9a7c9e0c755bf68447f7293e5729547750fe6be3`, plus the local asynchronous
fence-wait lifetime patch. Upstream LICENSE and ThirdPartyNotices are retained.
This directory is vendored source, not a submodule or a runtime DLL modification.

The public source-level defect is reported upstream as
[ComputeSharp issue #936](https://github.com/Sergio0694/ComputeSharp/issues/936).
The submitted body is `docs/upstream/ComputeSharpAsyncFenceWaitPublicBug.md`.
The separate detailed local draft contains machine/workload measurements and
was not published. Do not publish those additional diagnostics without approval.

`ComputeSharp-waitfix.patch` records the four source changes against that commit.
`ComputeSharp.Runtime/ComputeSharp.Runtime.csproj` builds only the runtime and its
shared D3D12 bindings for .NET 8. The upstream strong-name key is retained to
preserve compatibility with ComputeSharp.Core's friend-assembly declarations.
The build wrapper supplies the upstream runtime's module/platform attributes.
The application still uses the unmodified ComputeSharp.Core 3.2.0 and released
ComputeSharp 3.2.0 generator/build assets; only runtime compile/deployment assets
are replaced by the project reference. The wrapper's PackageId is deliberately
distinct, so it cannot shadow the original package during dependency resolution.

The local assembly has version 3.2.0.0 and informational version
`3.2.0+mandelbrot.waitfix.1.upstream.9a7c9e0`. Regression tests require that marker.

## Wait lifetime

- Register once with WT_EXECUTEONLYONCE and retain the native registration.
- A two-party atomic publication handshake gives cleanup to exactly one party,
  including when the callback runs before registration returns.
- The callback cancels nonblockingly, accepting ERROR_IO_PENDING only there.
  The publisher's early-completion path cancels blockingly outside the callback.
- Cancel before closing the event and releasing callback context/GCHandles.
- Setup failures drain submitted work before recycling its allocator and free
  acquired managed/native wait resources. A drain failure remains terminal;
  this patch does not redefine ComputeSharp's broader device-loss contract.
- Unexpected cancellation failures fault the awaiter and quarantine native
  resources rather than free objects whose OS ownership is uncertain.
- Source-generated last-error-aware bindings support DisableRuntimeMarshalling.

## Validation

Run the full suite in each application build configuration:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj --no-restore
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
./tools/AuditAsyncFenceLifetime.ps1
```

The suite includes 10,000 publication races, both forced native callback orderings,
2,000 native registrations with a handle-growth bound, last-error capture, and
20 quiet renderer frames with exact image and handle-growth checks. It does not
inject GPU removal, native allocation failure, or invalid thread-pool wait handles.
See `docs/ProductionStabilityValidation.md` for monitored production workloads.

When upstream supplies a reviewed fix, remove the runtime project reference and
restore normal ComputeSharp compile/runtime assets together. Update the marker
check and lifetime audit, then rerun full regressions and monitored validation.
