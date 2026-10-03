# Mandelbrot GPU Explorer

A Windows desktop Mandelbrot explorer built with C#, WPF, and ComputeSharp.
Click into the fractal to explore progressively deeper detail, with GPU compute
shaders doing the bulk of the orbit calculations and high-precision CPU
arithmetic supporting deep zooms.

This project was made public as an example of a fully Codex-designed application,
developed under human direction.

(Human here: I have a long history of using Mandelbrot set
visualizers as "Hello World" projects when I'm learning new technologies.  My last one was
an FPGA embedded system coupled with a desktop app for visualization.  But now we're in the
AI coding era, for better or worse I needed to learn the workflow.)

A central goal is to take full advantage of
available GPU compute capabilities through ComputeSharp for performance, while
maintaining numerical correctness and a responsive interface. The current
renderer uses ComputeSharp's default Direct3D 12 device; it does not distribute
work across multiple GPUs.

## Using the application

- **Left click:** zoom in by a factor of four, centered on the clicked point.
- **Right click:** zoom out by a factor of four, centered on the clicked point.
- **Back:** restore the previous viewport, including its high-precision coordinates.
- **Reset:** return to the full Mandelbrot set.
- **Resize:** render at the window's physical pixel resolution, accounting for
  display DPI and preserving the current center and vertical span.

The header displays the viewport coordinates and zoom information. The status
bar reports the rendering mode, iteration budget, reference passes, repaired and
unresolved pixels, and elapsed time. The iteration budget increases with zoom
depth, up to 32,768 iterations.

Zooming immediately scales the existing image as a preview. New results replace
it progressively, and histogram coloring uses the previous completed palette
until the new frame is complete. Rendering runs off the UI thread; navigation
and resize requests are coalesced so GPU render jobs do not overlap.

## Key design features

### GPU rendering with adaptive precision

ComputeSharp translates C# compute shaders into GPU programs executed through
Direct3D 12. The renderer chooses its arithmetic according to the workload:

1. **Direct FP64:** ordinary views calculate each pixel's orbit independently
   using GPU double precision.
2. **FP64 perturbation:** deep views use a CPU-generated MPFR reference orbit
   and calculate small deviations from it on the GPU.
3. **Double-double recovery:** when enough FP64 results are flagged as
   numerically uncertain, only those pixels are reevaluated using pairs of
   doubles. Trusted FP64 results are retained.

Reference rebasing and bivariate linear approximation (BLA) accelerate
perturbation rendering. BLA combines suitable iteration blocks into affine
approximations, with radius and error checks controlling when a block can be
used. Double-double arithmetic uses explicit fused multiply-add operations.

### High-precision coordinates and bounded repair

Viewport coordinates are stored with 384-bit MPFR arithmetic, preserving detail
through repeated zooms that ordinary doubles cannot represent. Additional
reference orbits target clusters of unresolved pixels, and GPU buffers are
reused across reference passes within a frame.

Remaining uncertain pixels can be repaired directly on the CPU using MPFR.
Repair work has a fixed iteration budget rather than a resolution-dependent
pixel allowance. If recovery cannot finish within the limits, unresolved pixels
remain explicitly classified and are reported in the status bar.

### Bounded GPU work and resource lifetime

Pixel batches and iteration slices limit individual submissions. A bounded
submission queue overlaps host recording with GPU execution, and periodic
readback supports progressive display without copying every slice. Resource
ownership and queue draining keep buffers alive until submitted work completes.

Device-loss checks and a submission-duration brake suspend rendering after a
GPU failure or excessive observed completion latency. These checks require an
application restart before further rendering; they cannot preempt a running
submission or guarantee watchdog safety on every GPU.

### Numerical regression coverage

The renderer regression suite checks raw FP64 and double-double results against
768-bit MPFR, as well as sparse mapping, buffer reuse, batch boundaries, 4K
dispatch coverage, repair budgets, progressive presentation, and queue/journal
behavior. Independent numerical baselines are retained for comparison.

These checks exercise defined fixtures and samples; they do not prove every
possible deep viewport correct or establish performance on every adapter.
Detailed validation and profiling evidence is in [docs](docs), with harness
instructions in [tests/RendererChecks/README.md](tests/RendererChecks/README.md).

## Build and run

Requirements:

- Windows x64.
- The .NET 8 SDK, or Visual Studio with .NET 8 and WPF development support.
- A Direct3D 12 adapter supporting double-precision compute.
- NuGet access for the first restore.

From the repository root, build and validate the Release configuration:

```powershell
dotnet run --configuration Release --project tests/RendererChecks/RendererChecks.csproj
```

The regression project builds the application through its project reference.
After the suite passes, launch:

```powershell
.\bin\Release\net8.0-windows\MandelbrotGpu.exe
```

For Debug validation, substitute `Debug` for `Release`. After dependencies are
restored, add `--no-restore` to the regression command. Every application build
must pass the suite in its matching configuration before being treated as ready
to use.

The build copies the native MPFR and GMP DLLs beside the executable. It uses
ComputeSharp 3.2.0 generators and build assets with a vendored, patched runtime
for asynchronous fence-wait lifetime handling. Keep the `third_party` sources
when cloning or building. Existing native-package dependencies can produce
NuGet compatibility warnings (`NU1701`).

## Source organization

The main renderer coordinates precision selection, GPU passes, sparse recovery,
and final coloring. Separate files contain the compute shaders, reference-orbit
and BLA algorithms, MPFR bindings, histogram coloring, progressive presentation,
and WPF interface. Small related types are grouped into files for render results,
viewport models, GPU device safety, dispatch journals, and diagnostics.

## License

The project is licensed under the [MIT License](LICENSE). Vendored ComputeSharp
code retains its [upstream license](third_party/ComputeSharp/LICENSE); dependency
licenses and notices continue to apply to their respective components.
