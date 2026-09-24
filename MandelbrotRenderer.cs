using ComputeSharp;
using System.Diagnostics;
using ComputeSharp.Descriptors;

namespace MandelbrotGpu;

public sealed partial class MandelbrotRenderer(int width, int height)
{
    // Above this scale, ordinary FP64 coordinates have enough spacing for the
    // full pixel grid, so a direct per-pixel GPU shader is fastest.
    private const double DirectFloat64ScaleLimit = 1E-12;

    // Large FP64 tails go to sparse DD evaluation. Retain trusted FP64 counts
    // so a precision fallback does not become a second full-frame render.
    private const int Float64FallbackGlitchLimit = 4096;

    // Bound worst-case MPFR work, independent of output resolution. A pixel
    // percentage created a quality cliff when resizing an otherwise identical view.
    private const int MaxExtraReferences = 32;
    private const long TargetFinalRepairIterations = 120_000_000;

    // Glitches tend to cluster spatially. Tiles give the rebase pass a cheap
    // way to pick new references near the densest remaining failures.
    private const int TileSize = 32;

    private readonly bool diagnostics = RendererDiagnostics.Enabled;
    // Harness-only stage timing on the ordinary no-journal path. No shipping
    // setting enables this, and GPU safety/lifetime policy is unchanged.
    internal bool ProfileTimings { get; init; }
    private bool MeasureTimings => diagnostics || ProfileTimings;
    private readonly GraphicsDevice device = GraphicsDevice.GetDefault();
    private RenderTimings timings = new();
    private List<BlaPassProfile>? blaPassProfiles;
    private Guid renderId;
    private int referenceSequence;
    private int dispatchBudget;
    private int dispatchAcceleration;
    private object? dispatchResources;
    private readonly int acceleration = Environment.GetEnvironmentVariable("MANDELBROT_ACCELERATION") switch
    {
        "none" => 0,
        "rebase" => 1,
        _ => 2
    };

    // Borrowed arrays: the callback must consume them before returning. Sparse
    // indices map compact DD output back to the original viewport.
    internal Action<int[], int, int, int[]?>? PublishPixels { get; init; }

    public RenderResult Render(MandelbrotViewport viewport, int maxIterations)
        => RenderWithMode(viewport, maxIterations, SelectMode(viewport.Scale));

    // Separate policy from execution so the regression/profiling harness can
    // compare initial precisions through the same recovery and safety pipeline.
    private RenderResult RenderWithMode(MandelbrotViewport viewport, int maxIterations, RenderMode initialMode)
    {
        using ViewportTelemetry? telemetry = ViewportTelemetry.Start(viewport, width, height, maxIterations);
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        try
        {
            RenderResult result = RenderCore(viewport, maxIterations, initialMode);
            if (MeasureTimings) timings.TotalMilliseconds = timer.Elapsed.TotalMilliseconds;
            telemetry?.Complete(result);
            return result;
        }
        catch (Exception ex) when (GpuDeviceFailure.TryGetCode(ex, out int code))
        {
            // RenderCore's using scopes have released the frame's resources.
            // Disposing removes the dead default device from ComputeSharp's
            // cache; the next renderer obtains a fresh device on the adapter.
            device.Dispose();
            throw new GpuRenderSuspendedException($"GPU device lost (0x{code:X8}). Rendering suspended; restart the application before retrying.", ex);
        }
    }

    private RenderResult RenderCore(MandelbrotViewport viewport, int maxIterations, RenderMode initialMode)
    {
        timings = new RenderTimings();
        blaPassProfiles?.Clear();
        renderId = diagnostics ? Guid.NewGuid() : default;
        referenceSequence = 0;
        // Render in three stages:
        //   1. Start with FP64 perturbation beyond the direct-coordinate limit.
        //   2. Let double-double mode add reference orbits for clustered
        //      perturbation glitches.
        //   3. Repair a bounded number of remaining pixels directly in MPFR.
        RenderMode mode = initialMode;
        int referencePasses = 1;
        int[] iterations;

        if (mode == RenderMode.DirectFloat64)
        {
            iterations = RenderDirect(viewport.ToFloat64View(), maxIterations);
        }
        else if (mode == RenderMode.PerturbationFloat64)
        {
            iterations = RenderPerturbationFloat64(viewport, maxIterations);
        }
        else if (mode == RenderMode.PerturbationDoubleDouble)
        {
            (iterations, referencePasses) = RenderPerturbationDoubleDouble(viewport, maxIterations);
        }
        else
        {
            throw new InvalidOperationException($"Unknown render mode {mode}.");
        }

        int glitchCount = CountGlitches(iterations);
        int initialGlitchCount = glitchCount;
        int float64GlitchCount = mode == RenderMode.PerturbationFloat64 ? glitchCount : 0;
        int repairedCount = 0;
        int finalRepairLimit = GetFinalRepairLimit(maxIterations);

        // The uncertainty heuristic may flag many pixels without invalidating
        // the rest of the frame. Upgrade only the flagged subset to DD.
        // Keep the inexpensive sparse-DD crossover separate from the final
        // MPFR ceiling: increasing repair headroom must not bypass useful DD.
        int fallbackLimit = Math.Min(finalRepairLimit, Math.Min(Float64FallbackGlitchLimit,
            Math.Max(1024, (int)Math.Ceiling((long)width * height * 0.005))));
        if (mode == RenderMode.PerturbationFloat64 && glitchCount > fallbackLimit)
        {
            mode = RenderMode.PerturbationDoubleDouble;
            using MpfrComplex referencePoint = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
            int[] active = Enumerable.Range(0, iterations.Length)
                .Where(index => iterations[index] == EscapeClassification.Glitch).ToArray();
            // Recovery only removes pixels from this list; retries fit in the
            // initial unresolved capacity rather than requiring a full frame.
            using PerturbationBuffers buffers = new(device, active.Length, maxIterations);
            int[] candidate = RenderPerturbationDoubleDouble(viewport, referencePoint, maxIterations, active, buffers);
            MergeResolved(iterations, active, candidate);
            referencePasses = 2 + AddExtraReferencePasses(viewport, iterations, maxIterations, buffers);
            glitchCount = CountGlitches(iterations);
            initialGlitchCount = glitchCount;
        }

        if (mode is RenderMode.PerturbationFloat64 or RenderMode.PerturbationDoubleDouble)
        {
            // Always spend the available repair allowance, even when the tail
            // exceeds it. Unprocessed pixels remain explicitly unresolved.
            if (glitchCount > 0 && finalRepairLimit > 0)
            {
                DiagnosticTimer repairTimer = DiagnosticTimer.StartNew(MeasureTimings);
                repairedCount = RepairGlitches(viewport, iterations, maxIterations);
                timings.RepairMilliseconds += repairTimer.Elapsed.TotalMilliseconds;
            }
        }

        int unresolvedGlitchCount = CountGlitches(iterations);
        DiagnosticTimer validationTimer = DiagnosticTimer.StartNew(MeasureTimings);
        RenderValidation? validation = diagnostics && Environment.GetEnvironmentVariable("MANDELBROT_VALIDATE") == "1"
            ? NumericalValidation.Check(viewport, iterations, width, height, maxIterations)
            : null;
        if (validation is not null)
            timings.ValidationMilliseconds += validationTimer.Elapsed.TotalMilliseconds;
        DiagnosticTimer colorTimer = DiagnosticTimer.StartNew(MeasureTimings);
        // Validation and all iteration callbacks are complete. Transfer this
        // array to the result after replacing counts with final colors.
        int[] pixels = HistogramColorizer.ColorizeInPlace(iterations, maxIterations, out int[] histogramPalette);
        timings.ColoringMilliseconds = colorTimer.Elapsed.TotalMilliseconds;

        return new RenderResult(
            pixels,
            mode,
            initialGlitchCount,
            repairedCount,
            unresolvedGlitchCount,
            referencePasses,
            finalRepairLimit,
            timings,
            validation)
        {
            HistogramPalette = histogramPalette,
            Float64GlitchCount = float64GlitchCount,
            UsedDoubleDoubleFallback = initialMode == RenderMode.PerturbationFloat64 && mode == RenderMode.PerturbationDoubleDouble
        };
    }

    private static RenderMode SelectMode(double scale)
    {
        if (scale >= DirectFloat64ScaleLimit)
        {
            return RenderMode.DirectFloat64;
        }

        // Depth alone is not a reason to use expensive double-double deltas.
        // Keep trusted FP64 results; escalate only pixels flagged by the error
        // model. The reference and viewport still use high-precision MPFR.
        return RenderMode.PerturbationFloat64;
    }

    private int[] RenderDirect(MandelbrotView view, int maxIterations)
    {
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];

        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);
        BeginPass(maxIterations, diagnostics ? new { outputBytes = (long)pixelCount * sizeof(int) } : null);

        // The compute shader writes one escape count per pixel. Coloring stays
        // on the CPU because histogram coloring needs a whole-image cumulative
        // distribution after every pixel has been evaluated.
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        int batchPixels = GpuDispatchPolicy.BatchPixels(RenderMode.DirectFloat64, maxIterations);
        for (int offset = 0; offset < pixelCount; offset += batchPixels)
        {
            Dispatch(RenderMode.DirectFloat64, offset,
                Math.Min(batchPixels, pixelCount - offset),
                new MandelbrotEscapeShader(
                    iterationBuffer,
                    view.Left,
                    view.Top,
                    view.Width / width,
                    view.Height / height,
                    width,
                    offset,
                    maxIterations));
            if (PublishPixels is not null)
            {
                int count = Math.Min(batchPixels, pixelCount - offset);
                iterationBuffer.CopyTo(iterations.AsSpan(offset, count), offset);
                PublishPixels(iterations, offset, count, null);
            }
        }

        timer.Restart();
        iterationBuffer.CopyTo(iterations);
        timings.ReadbackMilliseconds += timer.Elapsed.TotalMilliseconds;

        return iterations;
    }

    private int[] RenderPerturbationFloat64(MandelbrotViewport viewport, int maxIterations)
    {
        using MpfrComplex referencePoint = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        return RenderPerturbationFloat64(viewport, referencePoint, maxIterations);
    }

    private int[] RenderPerturbationFloat64(MandelbrotViewport viewport, MpfrComplex referencePoint, int maxIterations)
    {
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        DeepZoomReferenceOrbit reference = DeepZoomReferenceOrbit.Build(referencePoint, maxIterations);
        timings.ReferenceMilliseconds += timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];
        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);
        using ReadOnlyBuffer<double> referenceReal = device.AllocateReadOnlyBuffer(reference.RealHigh);
        using ReadOnlyBuffer<double> referenceImaginary = device.AllocateReadOnlyBuffer(reference.ImaginaryHigh);
        timings.UploadMilliseconds += timer.Elapsed.TotalMilliseconds;

        GetFloat64Deltas(viewport, referencePoint, out double leftDelta, out double topDelta, out double stepX, out double stepY);
        timer.Restart();
        BlaTable table = new(reference, DeltaBound(leftDelta, topDelta, stepX, stepY), acceleration >= 2 ? 1E-14 : 0);
        timings.BlaMilliseconds += timer.Elapsed.TotalMilliseconds;
        using ReadOnlyBuffer<double> bla = device.AllocateReadOnlyBuffer(table.Data);
        bool metricsEnabled = diagnostics && Environment.GetEnvironmentVariable("MANDELBROT_METRICS") == "1";
        using ReadWriteBuffer<int> metrics = device.AllocateReadWriteBuffer<int>(metricsEnabled ? pixelCount * 3 : 1);
        int sliceIterations = GpuDispatchPolicy.SliceIterations(maxIterations);
        int readbackSlices = GpuDispatchPolicy.ReadbackSlices();
        int inFlight = GpuDispatchPolicy.InFlightSubmissions();
        using GpuDeviceStatus? status = inFlight > 1 ? new(device) : null;
        int batchPixels = GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationFloat64, sliceIterations);
        using ReadWriteBuffer<double> state = device.AllocateReadWriteBuffer<double>(Math.Min(pixelCount, batchPixels) * 5);
        using ReadBackBuffer<int> readback = device.AllocateReadBackBuffer<int>(Math.Min(pixelCount, batchPixels));
        BeginPass(maxIterations, diagnostics ? new
        {
            outputBytes = (long)pixelCount * sizeof(int),
            referenceBytes = (long)reference.RealHigh.Length * 2 * sizeof(double),
            blaBytes = (long)table.Data.Length * sizeof(double),
            metricsBytes = (long)metrics.Length * sizeof(int),
            stateBytes = (long)state.Length * sizeof(double),
            readbackBytes = (long)readback.Length * sizeof(int)
        } : null, acceleration >= 2 && !table.HasSkips ? 1 : acceleration);

        timer.Restart();
        for (int offset = 0; offset < pixelCount; offset += batchPixels)
        {
            int count = Math.Min(batchPixels, pixelCount - offset);
            int completedSlices = 0;
            using DispatchJournalGroup? journal = CreateJournalGroup(RenderMode.PerturbationFloat64, maxIterations, sliceIterations);
            using BoundedGpuQueue queue = new(inFlight, status is null ? null : status.Check) { MeasureWait = MeasureTimings };
            for (int sliceStart = 0; sliceStart < maxIterations; sliceStart += sliceIterations)
            {
                int sliceEnd = Math.Min(sliceStart + sliceIterations, maxIterations);
                Dispatch(RenderMode.PerturbationFloat64, offset,
                    count,
                    new MandelbrotPerturbationFloat64Shader(
                        iterationBuffer,
                        referenceReal,
                        referenceImaginary,
                        bla,
                        metrics,
                        state,
                        table.LeafCount,
                        reference.Length,
                        acceleration >= 2 && !table.HasSkips ? 1 : acceleration,
                        metricsEnabled,
                        referencePoint.Real.ToDouble(),
                        referencePoint.Imaginary.ToDouble(),
                        leftDelta,
                        topDelta,
                        stepX,
                        stepY,
                        width,
                        offset,
                        sliceStart,
                        sliceEnd,
                        maxIterations), sliceStart, sliceEnd, journal, iterationBuffer, state, metrics, inFlight > 1 ? queue : null);
                // The copy queue cannot read outputs until every preceding compute
                // submission is consumed. Shared batch state stays ordered by UAV
                // barriers on the one existing compute queue, never parallel queues.
                bool pending = true;
                if (GpuDispatchPolicy.ShouldReadSlice(++completedSlices, readbackSlices, sliceEnd == maxIterations))
                {
                    queue.Drain();
                    pending = ReadSlice(iterationBuffer, readback, iterations.AsSpan(offset, count), offset, RenderMode.PerturbationFloat64);
                    PublishPixels?.Invoke(iterations, offset, count, null);
                }
                else if (MeasureTimings) timings.SkippedSliceReadbacks++;
                if (journal is { HasPending: false }) journal.Checkpoint(!pending || sliceEnd == maxIterations);
                if (!pending) break;
            }
        }
        timings.Float64SliceLoopMilliseconds += timer.Elapsed.TotalMilliseconds;

        EnsureComplete(iterations);
        if (MeasureTimings) timings.PerturbationPixelEvaluations += pixelCount;
        if (metricsEnabled) CollectMetrics(metrics, pixelCount);

        return iterations;
    }

    private (int[] Iterations, int ReferencePasses) RenderPerturbationDoubleDouble(MandelbrotViewport viewport, int maxIterations)
    {
        using PerturbationBuffers buffers = new(device, width * height, maxIterations);
        using MpfrComplex referencePoint = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        int[] pixelIndices = Enumerable.Range(0, width * height).ToArray();
        int[] iterations = RenderPerturbationDoubleDouble(viewport, referencePoint, maxIterations, pixelIndices, buffers);
        // Start with the viewport center as the first reference, then add more
        // references only where glitch classification proves they are needed.
        int referencePasses = 1 + AddExtraReferencePasses(viewport, iterations, maxIterations, buffers);

        return (iterations, referencePasses);
    }

    private int[] RenderPerturbationDoubleDouble(MandelbrotViewport viewport, MpfrComplex referencePoint, int maxIterations, int[] pixelIndices, PerturbationBuffers buffers)
    {
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        DeepZoomReferenceOrbit reference = DeepZoomReferenceOrbit.Build(referencePoint, maxIterations);
        timings.ReferenceMilliseconds += timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        int pixelCount = pixelIndices.Length;
        int[] iterations = new int[pixelCount];
        buffers.PixelIndices.CopyFrom(pixelIndices.AsSpan(), 0);
        buffers.RealHigh.CopyFrom(reference.RealHigh);
        buffers.RealLow.CopyFrom(reference.RealLow);
        buffers.ImaginaryHigh.CopyFrom(reference.ImaginaryHigh);
        buffers.ImaginaryLow.CopyFrom(reference.ImaginaryLow);
        timings.UploadMilliseconds += timer.Elapsed.TotalMilliseconds;

        GetDoubleDoubleDeltas(
            viewport,
            referencePoint,
            out DoubleDouble leftDelta,
            out DoubleDouble topDelta,
            out DoubleDouble stepX,
            out DoubleDouble stepY);

        DoubleDouble referenceReal = referencePoint.Real.ToDoubleDouble();
        DoubleDouble referenceImaginary = referencePoint.Imaginary.ToDoubleDouble();
        timer.Restart();
        double viewportBound = DeltaBound(leftDelta.High, topDelta.High, stepX.High, stepY.High);
        BlaTable table = new(reference, viewportBound, acceleration >= 2 ? 1E-18 : 0);
        BlaPassProfile? blaProfile = null;
        if (buffers.MetricsEnabled && BlaPassProfile.Enabled)
        {
            int usable = 0;
            double maxRadius = 0;
            for (int node = 1; node < table.LeafCount; node++)
                if (table.Data[node * BlaTable.Stride + 8] > 0)
                {
                    usable++;
                    maxRadius = Math.Max(maxRadius, table.Data[node * BlaTable.Stride + 8]);
                }
            blaProfile = new() { Pixels = pixelCount, ViewportBound = viewportBound,
                UsableNodes = usable, ReferenceLength = reference.Length,
                MaxRadius = maxRadius,
                GpuCountersAvailable = buffers.BlaProfileEnabled };
            (blaPassProfiles ??= []).Add(blaProfile);
        }
        timings.BlaMilliseconds += timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        buffers.Bla.CopyFrom(table.Data);
        timings.UploadMilliseconds += timer.Elapsed.TotalMilliseconds;

        int sliceIterations = GpuDispatchPolicy.SliceIterations(maxIterations);
        int readbackSlices = GpuDispatchPolicy.ReadbackSlices();
        int inFlight = GpuDispatchPolicy.InFlightSubmissions();
        int minimumBlaLength = GpuDispatchPolicy.BlaMinimumBlockLength();
        using GpuDeviceStatus? status = inFlight > 1 ? new(device) : null;
        int batchPixels = GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationDoubleDouble, sliceIterations);
        BeginPass(maxIterations, diagnostics ? new
        {
            outputBytes = (long)buffers.Output.Length * sizeof(int),
            indicesBytes = (long)buffers.PixelIndices.Length * sizeof(int),
            referenceBytes = (long)buffers.RealHigh.Length * 4 * sizeof(double),
            blaBytes = (long)buffers.Bla.Length * sizeof(double),
            metricsBytes = (long)buffers.Metrics.Length * sizeof(int),
            stateBytes = (long)buffers.State.Length * sizeof(double),
            readbackBytes = (long)buffers.Readback.Length * sizeof(int)
        } : null, acceleration >= 2 && !table.HasSkips ? 1 : acceleration);
        timer.Restart();
        for (int workOffset = 0; workOffset < pixelCount; workOffset += batchPixels)
        {
            int count = Math.Min(pixelCount - workOffset, batchPixels);
            int completedSlices = 0;
            using DispatchJournalGroup? journal = CreateJournalGroup(RenderMode.PerturbationDoubleDouble, maxIterations, sliceIterations);
            using BoundedGpuQueue queue = new(inFlight, status is null ? null : status.Check) { MeasureWait = MeasureTimings };
            for (int sliceStart = 0; sliceStart < maxIterations; sliceStart += sliceIterations)
            {
                int sliceEnd = Math.Min(sliceStart + sliceIterations, maxIterations);
                Dispatch(RenderMode.PerturbationDoubleDouble, workOffset,
                    count,
                    new MandelbrotPerturbationDoubleDoubleShader(
                        buffers.Output,
                        buffers.PixelIndices,
                        buffers.RealHigh,
                        buffers.RealLow,
                        buffers.ImaginaryHigh,
                        buffers.ImaginaryLow,
                        buffers.Bla,
                        buffers.Metrics,
                        buffers.State,
                        table.LeafCount,
                        reference.Length,
                        acceleration >= 2 && !table.HasSkips ? 1 : acceleration,
                        minimumBlaLength,
                        buffers.MetricsEnabled,
#if DEBUG
                        buffers.BlaProfileEnabled,
#endif
                        referenceReal.High,
                        referenceReal.Low,
                        referenceImaginary.High,
                        referenceImaginary.Low,
                        leftDelta.High,
                        leftDelta.Low,
                        topDelta.High,
                        topDelta.Low,
                        stepX.High,
                        stepX.Low,
                        stepY.High,
                        stepY.Low,
                        width,
                        workOffset,
                        sliceStart,
                        sliceEnd,
                        maxIterations), sliceStart, sliceEnd, journal, buffers.Output, buffers.State, buffers.Metrics, inFlight > 1 ? queue : null);
                bool pending = true;
                if (GpuDispatchPolicy.ShouldReadSlice(++completedSlices, readbackSlices, sliceEnd == maxIterations))
                {
                    queue.Drain();
                    pending = ReadSlice(buffers.Output, buffers.Readback, iterations.AsSpan(workOffset, count), workOffset, RenderMode.PerturbationDoubleDouble);
                    PublishPixels?.Invoke(iterations, workOffset, count, pixelIndices);
                }
                else if (MeasureTimings) timings.SkippedSliceReadbacks++;
                if (journal is { HasPending: false }) journal.Checkpoint(!pending || sliceEnd == maxIterations);
                if (!pending) break;
            }
        }
        timings.DoubleDoubleSliceLoopMilliseconds += timer.Elapsed.TotalMilliseconds;
        EnsureComplete(iterations);
        if (MeasureTimings) timings.PerturbationPixelEvaluations += pixelCount;
        if (buffers.MetricsEnabled) CollectMetrics(buffers.Metrics, pixelCount,
            buffers.BlaProfileEnabled ? BlaPassProfile.MetricStride : 3, blaProfile);

        return iterations;
    }

    private void BeginPass(int maxIterations, object? resources, int shaderAcceleration = 0)
    {
        if (!diagnostics) return;
        referenceSequence++;
        dispatchBudget = maxIterations;
        dispatchAcceleration = shaderAcceleration;
        dispatchResources = resources;
    }

    private bool ReadSlice(ReadWriteBuffer<int> output, ReadBackBuffer<int> readback, Span<int> destination, int offset, RenderMode mode)
    {
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        // Explicit staging avoids the temporary native resource used by CopyTo(Span).
        output.CopyTo(readback, offset, 0, destination.Length);
        readback.Span[..destination.Length].CopyTo(destination);
        timings.ReadbackMilliseconds += timer.Elapsed.TotalMilliseconds;
        if (MeasureTimings)
        {
            timings.SliceReadbackCount++;
            timings.SliceReadbackBytes += (long)destination.Length * sizeof(int);
            if (mode == RenderMode.PerturbationFloat64) timings.Float64SliceReadbackCount++;
            else timings.DoubleDoubleSliceReadbackCount++;
        }
        timer.Restart();
        bool pending = false;
        foreach (int value in destination)
        {
            if (value != EscapeClassification.Pending) continue;
            pending = true;
            break;
        }
        timings.SliceScanMilliseconds += timer.Elapsed.TotalMilliseconds;
        return pending;
    }

    private static void EnsureComplete(int[] iterations)
    {
        if (iterations.Contains(EscapeClassification.Pending))
            throw new InvalidOperationException("GPU slice state remained pending at the iteration budget.");
    }

    private void Dispatch<T>(RenderMode mode, int offset, int count, in T shader, int sliceStart = 0, int sliceEnd = 0,
        DispatchJournalGroup? journal = null, ReadWriteBuffer<int>? output = null,
        ReadWriteBuffer<double>? state = null, ReadWriteBuffer<int>? metrics = null, BoundedGpuQueue? queue = null,
        ReadWriteBuffer<float>? floatState = null)
        where T : struct, IComputeShader, IComputeShaderDescriptor<T>
    {
        queue?.MakeRoom();
        if (journal?.NeedsCheckpoint == true)
        {
            queue?.Drain();
            journal.Checkpoint(false);
        }
        Guid submission = diagnostics ? Guid.NewGuid() : default;
        if (MeasureTimings && mode == RenderMode.PerturbationDoubleDouble) timings.DoubleDoubleDispatchedThreads += count;
        if (diagnostics)
        {
            object begin = new
            {
                phase = "begin",
                submission,
                journalGroup = journal?.Id,
                renderId,
                utc = DateTime.UtcNow,
                adapter = device.Name,
                adapterLuid = device.Luid.ToString(),
                dedicatedMemoryBytes = (ulong)device.DedicatedMemorySize,
                sharedMemoryBytes = (ulong)device.SharedMemorySize,
                referencePass = referenceSequence,
                iterationBudget = dispatchBudget,
                acceleration = dispatchAcceleration,
                requestedAcceleration = acceleration,
                readbackSlices = mode == RenderMode.DirectFloat64 ? 0 : GpuDispatchPolicy.ReadbackSlices(),
                inFlightLimit = queue is null ? 1 : GpuDispatchPolicy.InFlightSubmissions(),
                resources = dispatchResources,
                mode = mode.ToString(),
                kernel = typeof(T).Name,
                width,
                height,
                offset,
                count,
                sliceStart,
                sliceEnd
            };
            if (journal is null) WriteJournal(mode, begin);
            else journal.Begin(begin, sliceStart);
        }
        // Profiling includes recording/setup; the safety clock below starts
        // only when the recorded command list is about to be submitted.
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        double completionMilliseconds;
        try
        {
            // Explicit contexts exclude cold pipeline creation inside For from
            // the safety clock for direct, FP64, and double-double work alike.
            ComputeContext context = device.CreateComputeContext();
            bool transferred = false;
            try
            {
                context.For(count, in shader);
                if (output is not null)
                {
                    // Keep dependent UAV writes ordered between resumed slices.
                    context.Barrier(output);
                    if (floatState is not null) context.Barrier(floatState);
                    else context.Barrier(state!);
                    context.Barrier(metrics!);
                }
                long submitted = Stopwatch.GetTimestamp();
                if (queue is not null)
                {
                    // DisposeAsync submits immediately and transfers native
                    // list/allocator ownership to the library's fence callback.
                    // Never dispose the context twice, including on failure.
                    transferred = true;
                    Task completion = context.DisposeAsync().AsTask();
                    if (MeasureTimings)
                    {
                        double submit = timer.Elapsed.TotalMilliseconds;
                        AddDispatchTime(mode, submit);
                        timings.SubmissionMilliseconds += submit;
                        if (mode == RenderMode.PerturbationFloat64) timings.Float64SubmissionMilliseconds += submit;
                        if (mode == RenderMode.PerturbationDoubleDouble) timings.DoubleDoubleSubmissionMilliseconds += submit;
                    }
                    queue.AddTimed(completion, submitted, (wait, latency) =>
                    {
                        if (MeasureTimings)
                        {
                            AddDispatchTime(mode, wait);
                            timings.CompletionWaitMilliseconds += wait;
                            if (mode == RenderMode.PerturbationFloat64) timings.Float64CompletionWaitMilliseconds += wait;
                            if (mode == RenderMode.PerturbationDoubleDouble) timings.DoubleDoubleCompletionWaitMilliseconds += wait;
                        }
                        CompleteDispatch(mode, submission, journal, latency);
                    }, ex => FailDispatch(mode, submission, journal, ex));
                    if (MeasureTimings)
                    {
                        timings.AsyncDispatchCount++;
                        timings.MaxInFlightSubmissions = Math.Max(timings.MaxInFlightSubmissions, queue.Peak);
                    }
                    return;
                }
                transferred = true;
                context.Dispose();
                completionMilliseconds = Stopwatch.GetElapsedTime(submitted).TotalMilliseconds;
            }
            finally { if (!transferred) context.Dispose(); }
        }
        catch (Exception ex)
        {
            FailDispatch(mode, submission, journal, ex);
            throw;
        }
        double milliseconds = timer.Elapsed.TotalMilliseconds;
        AddDispatchTime(mode, milliseconds);
        CompleteDispatch(mode, submission, journal, completionMilliseconds);
    }

    private void FailDispatch(RenderMode mode, Guid submission, DispatchJournalGroup? journal, Exception ex)
    {
        if (!diagnostics) return;
        // Preserve native/submission failures even if the evidence write fails.
        try
        {
            object failed = new { phase = "failed", submission, journalGroup = journal?.Id, utc = DateTime.UtcNow, error = ex.ToString() };
            if (journal is null) WriteJournal(mode, failed);
            else journal.Fail(failed);
        }
        catch { }
    }

    private void AddDispatchTime(RenderMode mode, double milliseconds)
    {
        if (!MeasureTimings) return;
        timings.DispatchMilliseconds += milliseconds;
        if (mode == RenderMode.PerturbationFloat64) timings.Float64DispatchMilliseconds += milliseconds;
        if (mode == RenderMode.PerturbationDoubleDouble) timings.DoubleDoubleDispatchMilliseconds += milliseconds;
    }

    private void CompleteDispatch(RenderMode mode, Guid submission, DispatchJournalGroup? journal, double milliseconds)
    {
        if (MeasureTimings)
        {
            timings.DispatchCount++;
            timings.MaxDispatchMilliseconds = Math.Max(timings.MaxDispatchMilliseconds, milliseconds);
        }
        if (diagnostics)
        {
            object end = new { phase = "end", submission, journalGroup = journal?.Id, utc = DateTime.UtcNow, milliseconds };
            if (journal is null) WriteJournal(mode, end);
            else journal.Complete(end);
        }
        // This can stop subsequent submissions, not preempt the one just completed.
        // Submission-to-observed-completion remains a host latency upper bound,
        // not a hardware timestamp or watchdog protection. Recording and late
        // FIFO consumption are excluded; queue residency is deliberately retained.
        if (DispatchJournal.ShouldSuspend(milliseconds))
        {
            Exception? checkpointFailure = null;
            try { journal?.Stop(); }
            catch (Exception ex) { checkpointFailure = ex; }
            // A logging error must not turn the brake into a resumable UI error.
            string evidence = diagnostics ? $"; log: {DispatchJournal.LogPath}" : string.Empty;
            throw new GpuRenderSuspendedException($"GPU submission completion took {milliseconds:n0} ms. Rendering suspended; restart the application{evidence}.", checkpointFailure);
        }
    }

    private DispatchJournalGroup? CreateJournalGroup(RenderMode mode, int budget, int sliceIterations) =>
        diagnostics ? new(budget, sliceIterations, (write, records, intent) => RecordJournalTiming(mode, write, records, intent)) : null;

    private void WriteJournal(RenderMode mode, object entry)
    {
        JournalWriteTiming write = DispatchJournal.Write(entry);
        RecordJournalTiming(mode, write, 1, false);
    }

    private void RecordJournalTiming(RenderMode mode, JournalWriteTiming write, int records, bool groupIntent)
    {
        timings.JournalMilliseconds += write.TotalMilliseconds;
        timings.JournalSerializationMilliseconds += write.SerializationMilliseconds;
        timings.JournalFileMilliseconds += write.FileMilliseconds;
        timings.JournalWriteCount++;
        timings.JournalRecordCount += records;
        if (groupIntent) timings.JournalGroupCount++;
        if (mode == RenderMode.PerturbationFloat64) timings.Float64JournalMilliseconds += write.TotalMilliseconds;
        if (mode == RenderMode.PerturbationDoubleDouble) timings.DoubleDoubleJournalMilliseconds += write.TotalMilliseconds;
    }

    private double DeltaBound(double left, double top, double stepX, double stepY)
    {
        double x = Math.Max(Math.Abs(left), Math.Abs(left + width * stepX));
        double y = Math.Max(Math.Abs(top), Math.Abs(top - height * stepY));
        return Math.Sqrt(x * x + y * y) * (1 + 1E-12);
    }

    private void CollectMetrics(ReadWriteBuffer<int> buffer, int count, int stride = 3, BlaPassProfile? profile = null)
    {
        int[] values = new int[count * stride];
        buffer.CopyTo(values.AsSpan(), 0);
        for (int index = 0; index < count; index++)
        {
            timings.Rebases += values[index * stride];
            timings.SkippedIterations += values[index * stride + 1];
            timings.ScalarIterations += values[index * stride + 2];
            if (profile is not null)
            {
                profile.Rebases += values[index * stride];
                profile.SkippedIterations += values[index * stride + 1];
                profile.ScalarIterations += values[index * stride + 2];
                if (profile.GpuCountersAvailable)
                    for (int counter = 0; counter < BlaPassProfile.CounterCount; counter++)
                        profile.Counters[counter] += values[index * stride + 3 + counter];
            }
        }
    }

    private int AddExtraReferencePasses(MandelbrotViewport viewport, int[] iterations, int maxIterations, PerturbationBuffers buffers)
    {
        int referencePasses = 0;
        int previousGlitches = CountGlitches(iterations);
        if (previousGlitches <= GetFinalRepairLimit(maxIterations)) return 0;
        // Keep the raster-ordered unresolved tail between retries. A failed
        // reference does not change it; a successful one only removes entries.
        // Repeated full-image scans are particularly costly for thin glitch
        // bands in large images whose references cannot resolve the band.
        int[] activeIndices = Enumerable.Range(0, iterations.Length)
            .Where(index => iterations[index] == EscapeClassification.Glitch).ToArray();
        HashSet<int> failedTiles = [];

        for (int pass = 0; pass < MaxExtraReferences; pass++)
        {
            // More references are not useful once direct repair can finish the
            // tail within its bounded budget. Previously we still spent all
            // 32 retries on a few thousand persistently uncertain pixels.
            if (previousGlitches <= GetFinalRepairLimit(maxIterations)) return referencePasses;
            GlitchTile? tile = FindWorstActiveGlitchTile(activeIndices, failedTiles);

            if (tile is null)
            {
                // No eligible glitch tile remains. Either the frame is clean or
                // every remaining tile already failed to improve.
                return referencePasses;
            }

            // Rebase on a real glitched pixel near the densest tile's centroid.
            // That tends to cover the local cluster better than repeatedly
            // using the viewport center.
            using MpfrComplex referencePoint = viewport.PointAtPixel(tile.Value.ReferenceX, tile.Value.ReferenceY, width, height);
            // Dispatch only the unresolved tail. The compact output uses the
            // same order as this index list, so successful pixels need no work.
            int[] candidate = RenderPerturbationDoubleDouble(viewport, referencePoint, maxIterations, activeIndices, buffers);
            referencePasses++;
            int resolved = MergeResolved(iterations, activeIndices, candidate);

            int remainingGlitches = previousGlitches - resolved;

            if (resolved == 0 || remainingGlitches >= previousGlitches)
            {
                // Avoid burning every reference pass on the same unhelpful
                // region. Excluding the tile lets the next iteration try a
                // different cluster.
                failedTiles.Add(tile.Value.TileIndex);
                continue;
            }

            // A useful pass can change the glitch landscape, so let the next
            // reference choose from all tiles again.
            failedTiles.Clear();
            previousGlitches = remainingGlitches;
            activeIndices = activeIndices.Where(index => iterations[index] == EscapeClassification.Glitch).ToArray();
        }

        return referencePasses;
    }

    private static int MergeResolved(int[] iterations, int[] activeIndices, int[] candidate)
    {
        int resolved = 0;
        for (int index = 0; index < activeIndices.Length; index++)
        {
            if (iterations[activeIndices[index]] == EscapeClassification.Glitch && candidate[index] != EscapeClassification.Glitch)
            {
                iterations[activeIndices[index]] = candidate[index];
                resolved++;
            }
        }
        return resolved;
    }

    private GlitchTile? FindWorstGlitchTile(int[] iterations, HashSet<int> excludedTiles)
        => FindWorstActiveGlitchTile(Enumerable.Range(0, iterations.Length)
            .Where(index => iterations[index] == EscapeClassification.Glitch).ToArray(), excludedTiles);

    private GlitchTile? FindWorstActiveGlitchTile(int[] activeIndices, HashSet<int> excludedTiles)
    {
        int tileColumns = (width + TileSize - 1) / TileSize;
        int tileRows = (height + TileSize - 1) / TileSize;
        int[] counts = new int[tileColumns * tileRows];
        long[] sumX = new long[counts.Length];
        long[] sumY = new long[counts.Length];

        foreach (int index in activeIndices)
        {
            int x = index % width;
            int y = index / width;
            int tileIndex = (y / TileSize) * tileColumns + (x / TileSize);
            counts[tileIndex]++;
            sumX[tileIndex] += x;
            sumY[tileIndex] += y;
        }

        int bestTileIndex = -1;
        int bestCount = 0;

        for (int i = 0; i < counts.Length; i++)
        {
            if (!excludedTiles.Contains(i) && counts[i] > bestCount)
            {
                bestTileIndex = i;
                bestCount = counts[i];
            }
        }

        if (bestTileIndex < 0)
        {
            return null;
        }

        int centerX = (int)(sumX[bestTileIndex] / bestCount);
        int centerY = (int)(sumY[bestTileIndex] / bestCount);
        int referenceX = centerX;
        int referenceY = centerY;
        long bestDistanceSquared = long.MaxValue;

        // Pick an actual glitch pixel closest to the tile centroid. The exact
        // pixel can be converted to an MPFR coordinate and used as the new
        // reference point without inventing a synthetic center.
        foreach (int index in activeIndices)
        {
            int x = index % width;
            int y = index / width;
            int tileIndex = (y / TileSize) * tileColumns + (x / TileSize);

            if (tileIndex != bestTileIndex)
            {
                continue;
            }

            long dx = x - centerX;
            long dy = y - centerY;
            long distanceSquared = dx * dx + dy * dy;

            if (distanceSquared < bestDistanceSquared)
            {
                referenceX = x;
                referenceY = y;
                bestDistanceSquared = distanceSquared;
            }
        }

        return new GlitchTile(bestTileIndex, referenceX, referenceY, bestCount);
    }

    private readonly record struct GlitchTile(int TileIndex, int ReferenceX, int ReferenceY, int GlitchCount);

    private static int CountGlitches(int[] iterations)
    {
        int count = 0;

        foreach (int iteration in iterations)
        {
            if (iteration == EscapeClassification.Glitch)
            {
                count++;
            }
        }

        return count;
    }

    private int GetFinalRepairLimit(int maxIterations)
    {
        // This is an iteration-work ceiling, not a wall-clock deadline. Do not
        // apply a minimum after this division: that would violate the ceiling.
        int pixelCount = width * height;
        int iterationWeightedLimit = (int)(TargetFinalRepairIterations / global::System.Math.Max(maxIterations, 1));
        return global::System.Math.Min(pixelCount, iterationWeightedLimit);
    }

    private int RepairGlitches(MandelbrotViewport viewport, int[] iterations, int maxIterations)
    {
        int[] glitchIndexes = iterations
            .Select((iteration, index) => iteration == EscapeClassification.Glitch ? index : -1)
            .Where(index => index >= 0)
            .ToArray();
        var repaired = RepairWithinBudget(glitchIndexes, maxIterations, TargetFinalRepairIterations, index =>
        {
            // Each repair is independent, so CPU parallelism is useful here.
            // The cap above keeps this from dominating normal interactive use.
            int x = index % width;
            int y = index / width;
            using MpfrComplex c = viewport.PointAtPixel(x, y, width, height);
            int result = iterations[index] = MpfrMandelbrot.EscapeIterations(c, maxIterations);
            PublishPixels?.Invoke(iterations, index, 1, null);
            return result;
        });
        if (MeasureTimings) timings.RepairIterations += repaired.Work;
        return repaired.Count;
    }

    // Reserve each wave's worst-case work before dispatching CPU workers. Once
    // it joins, refund early escapes. No racing worker can overdraw the budget,
    // and selection remains deterministic even when the allowance is exhausted.
    private static (int Count, long Work) RepairWithinBudget(int[] indices, int maxIterations, long budget, Func<int, int> evaluate)
    {
        int count = 0;
        long work = 0;
        if (maxIterations <= 0) return (0, 0);
        while (count < indices.Length)
        {
            int wave = (int)Math.Min(indices.Length - count, (budget - work) / maxIterations);
            if (wave <= 0) break;
            long waveWork = 0;
            int start = count;
            Parallel.For(0, wave, offset =>
            {
                int result = evaluate(indices[start + offset]);
                // Escape i performs i updates plus the final radius check.
                // Interior performs exactly maxIterations loop evaluations.
                long charged = result < 0 ? maxIterations : Math.Min((long)maxIterations, (long)result + 1);
                Interlocked.Add(ref waveWork, charged);
            });
            work += waveWork;
            count += wave;
        }
        return (count, work);
    }

    private void GetFloat64Deltas(MandelbrotViewport viewport, MpfrComplex referencePoint, out double leftDelta, out double topDelta, out double stepX, out double stepY)
    {
        using MpfrFloat widthFloat = viewport.Width;
        using MpfrFloat halfWidth = widthFloat.Multiply(0.5);
        using MpfrFloat halfHeight = viewport.Height.Multiply(0.5);
        using MpfrFloat left = viewport.CenterX.Subtract(halfWidth);
        using MpfrFloat top = viewport.CenterY.Add(halfHeight);
        using MpfrFloat leftDeltaFloat = left.Subtract(referencePoint.Real);
        using MpfrFloat topDeltaFloat = top.Subtract(referencePoint.Imaginary);
        using MpfrFloat stepXFloat = widthFloat.Multiply(1.0 / width);
        using MpfrFloat stepYFloat = viewport.Height.Multiply(1.0 / height);

        leftDelta = leftDeltaFloat.ToDouble();
        topDelta = topDeltaFloat.ToDouble();
        stepX = stepXFloat.ToDouble();
        stepY = stepYFloat.ToDouble();
    }

    private void GetDoubleDoubleDeltas(
        MandelbrotViewport viewport,
        MpfrComplex referencePoint,
        out DoubleDouble leftDelta,
        out DoubleDouble topDelta,
        out DoubleDouble stepX,
        out DoubleDouble stepY)
    {
        using MpfrFloat widthFloat = viewport.Width;
        using MpfrFloat halfWidth = widthFloat.Multiply(0.5);
        using MpfrFloat halfHeight = viewport.Height.Multiply(0.5);
        using MpfrFloat left = viewport.CenterX.Subtract(halfWidth);
        using MpfrFloat top = viewport.CenterY.Add(halfHeight);
        using MpfrFloat leftDeltaFloat = left.Subtract(referencePoint.Real);
        using MpfrFloat topDeltaFloat = top.Subtract(referencePoint.Imaginary);
        using MpfrFloat stepXFloat = widthFloat.Multiply(1.0 / width);
        using MpfrFloat stepYFloat = viewport.Height.Multiply(1.0 / height);

        leftDelta = leftDeltaFloat.ToDoubleDouble();
        topDelta = topDeltaFloat.ToDoubleDouble();
        stepX = stepXFloat.ToDoubleDouble();
        stepY = stepYFloat.ToDoubleDouble();
    }
}
