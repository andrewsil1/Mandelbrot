using ComputeSharp;

namespace MandelbrotGpu;

public sealed class MandelbrotRenderer(int width, int height)
{
    // Above this scale, ordinary FP64 coordinates have enough spacing for the
    // full pixel grid, so a direct per-pixel GPU shader is fastest.
    private const double DirectFloat64ScaleLimit = 1E-12;

    // Between the two scale limits, FP64 perturbation usually provides the
    // best balance: MPFR computes one reference orbit, and the GPU evaluates
    // per-pixel deltas with hardware doubles.
    private const double PerturbationFloat64ScaleLimit = 1E-26;

    // If FP64 perturbation flags too many pixels, restart the frame in
    // double-double mode rather than spending the final MPFR repair budget on
    // what is likely a broad precision problem.
    private const int Float64FallbackGlitchLimit = 4096;

    // The final repair cap is deliberately adaptive. A percentage cap scales
    // with large render targets, while the iteration budget prevents deep
    // frames from quietly becoming long CPU jobs.
    private const int MinFinalRepairPixels = 1024;
    private const int MaxExtraReferences = 32;
    private const double MaxFinalRepairPixelFraction = 0.005;
    private const long TargetFinalRepairIterations = 120_000_000;

    // Glitches tend to cluster spatially. Tiles give the rebase pass a cheap
    // way to pick new references near the densest remaining failures.
    private const int TileSize = 32;

    private readonly GraphicsDevice device = GraphicsDevice.GetDefault();

    public RenderResult Render(MandelbrotViewport viewport, int maxIterations)
    {
        // Render in three stages:
        //   1. Pick the cheapest precision mode appropriate for the scale.
        //   2. Let double-double mode add reference orbits for clustered
        //      perturbation glitches.
        //   3. Repair a bounded number of remaining pixels directly in MPFR.
        RenderMode mode = SelectMode(viewport.Scale);
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
        int repairedCount = 0;
        int finalRepairLimit = GetFinalRepairLimit(maxIterations);

        // A noisy FP64 perturbation result usually means the whole viewport is
        // past FP64's useful perturbation range. Re-rendering with DD is more
        // useful than trying to patch thousands of pixels individually.
        if (mode == RenderMode.PerturbationFloat64 && glitchCount > Float64FallbackGlitchLimit)
        {
            mode = RenderMode.PerturbationDoubleDouble;
            (iterations, referencePasses) = RenderPerturbationDoubleDouble(viewport, maxIterations);
            glitchCount = CountGlitches(iterations);
            initialGlitchCount = glitchCount;
        }

        if (mode is RenderMode.PerturbationFloat64 or RenderMode.PerturbationDoubleDouble)
        {
            // If multi-reference rendering leaves only a small enough tail,
            // solve those pixels exactly with MPFR. Larger tails are left
            // visible and reported in the status line.
            if (glitchCount <= finalRepairLimit)
            {
                repairedCount = RepairGlitches(viewport, iterations, maxIterations);
            }
        }

        int unresolvedGlitchCount = CountGlitches(iterations);

        return new RenderResult(
            HistogramColorizer.Colorize(iterations, maxIterations),
            mode,
            initialGlitchCount,
            repairedCount,
            unresolvedGlitchCount,
            referencePasses,
            finalRepairLimit);
    }

    private static RenderMode SelectMode(double scale)
    {
        if (scale >= DirectFloat64ScaleLimit)
        {
            return RenderMode.DirectFloat64;
        }

        if (scale >= PerturbationFloat64ScaleLimit)
        {
            return RenderMode.PerturbationFloat64;
        }

        return RenderMode.PerturbationDoubleDouble;
    }

    private int[] RenderDirect(MandelbrotView view, int maxIterations)
    {
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];

        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);

        // The compute shader writes one escape count per pixel. Coloring stays
        // on the CPU because histogram coloring needs a whole-image cumulative
        // distribution after every pixel has been evaluated.
        device.For(
            width,
            height,
            new MandelbrotEscapeShader(
                iterationBuffer,
                view.Left,
                view.Top,
                view.Width / width,
                view.Height / height,
                width,
                maxIterations));

        iterationBuffer.CopyTo(iterations);

        return iterations;
    }

    private int[] RenderPerturbationFloat64(MandelbrotViewport viewport, int maxIterations)
    {
        using MpfrComplex referencePoint = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        return RenderPerturbationFloat64(viewport, referencePoint, maxIterations);
    }

    private int[] RenderPerturbationFloat64(MandelbrotViewport viewport, MpfrComplex referencePoint, int maxIterations)
    {
        DeepZoomReferenceOrbit reference = DeepZoomReferenceOrbit.Build(referencePoint, maxIterations);
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];
        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);
        using ReadOnlyBuffer<double> referenceReal = device.AllocateReadOnlyBuffer(reference.RealHigh);
        using ReadOnlyBuffer<double> referenceImaginary = device.AllocateReadOnlyBuffer(reference.ImaginaryHigh);

        GetFloat64Deltas(viewport, referencePoint, out double leftDelta, out double topDelta, out double stepX, out double stepY);

        device.For(
            width,
            height,
            new MandelbrotPerturbationFloat64Shader(
                iterationBuffer,
                referenceReal,
                referenceImaginary,
                referencePoint.Real.ToDouble(),
                referencePoint.Imaginary.ToDouble(),
                leftDelta,
                topDelta,
                stepX,
                stepY,
                width,
                maxIterations));

        iterationBuffer.CopyTo(iterations);

        return iterations;
    }

    private (int[] Iterations, int ReferencePasses) RenderPerturbationDoubleDouble(MandelbrotViewport viewport, int maxIterations)
    {
        using MpfrComplex referencePoint = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        int[] iterations = RenderPerturbationDoubleDouble(viewport, referencePoint, maxIterations);
        // Start with the viewport center as the first reference, then add more
        // references only where glitch classification proves they are needed.
        int referencePasses = 1 + AddExtraReferencePasses(viewport, iterations, maxIterations);

        return (iterations, referencePasses);
    }

    private int[] RenderPerturbationDoubleDouble(MandelbrotViewport viewport, MpfrComplex referencePoint, int maxIterations)
    {
        DeepZoomReferenceOrbit reference = DeepZoomReferenceOrbit.Build(referencePoint, maxIterations);
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];

        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);
        using ReadOnlyBuffer<double> referenceRealHigh = device.AllocateReadOnlyBuffer(reference.RealHigh);
        using ReadOnlyBuffer<double> referenceRealLow = device.AllocateReadOnlyBuffer(reference.RealLow);
        using ReadOnlyBuffer<double> referenceImaginaryHigh = device.AllocateReadOnlyBuffer(reference.ImaginaryHigh);
        using ReadOnlyBuffer<double> referenceImaginaryLow = device.AllocateReadOnlyBuffer(reference.ImaginaryLow);

        GetDoubleDoubleDeltas(
            viewport,
            referencePoint,
            out DoubleDouble leftDelta,
            out DoubleDouble topDelta,
            out DoubleDouble stepX,
            out DoubleDouble stepY);

        DoubleDouble referenceReal = referencePoint.Real.ToDoubleDouble();
        DoubleDouble referenceImaginary = referencePoint.Imaginary.ToDoubleDouble();

        device.For(
            width,
            height,
            new MandelbrotPerturbationDoubleDoubleShader(
                iterationBuffer,
                referenceRealHigh,
                referenceRealLow,
                referenceImaginaryHigh,
                referenceImaginaryLow,
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
                maxIterations));

        iterationBuffer.CopyTo(iterations);

        return iterations;
    }

    private int AddExtraReferencePasses(MandelbrotViewport viewport, int[] iterations, int maxIterations)
    {
        int referencePasses = 0;
        int previousGlitches = CountGlitches(iterations);
        HashSet<int> failedTiles = [];

        for (int pass = 0; pass < MaxExtraReferences; pass++)
        {
            GlitchTile? tile = FindWorstGlitchTile(iterations, failedTiles);

            if (tile is null)
            {
                // No eligible glitch tile remains. Either the frame is clean or
                // every remaining tile already failed to improve.
                return referencePasses;
            }

            // Rebase on a real glitched pixel near the densest tile's centroid.
            // That tends to cover the local cluster better than repeatedly
            // using the viewport center.
            using MpfrComplex referencePoint = viewport.PointAt((tile.Value.ReferenceX + 0.5) / width, (tile.Value.ReferenceY + 0.5) / height);
            int[] candidate = RenderPerturbationDoubleDouble(viewport, referencePoint, maxIterations);
            referencePasses++;
            int resolved = 0;

            for (int index = 0; index < iterations.Length; index++)
            {
                // Only replace pixels that were previously untrusted. Existing
                // non-glitch values remain tied to the earliest successful pass.
                if (iterations[index] == EscapeClassification.Glitch && candidate[index] != EscapeClassification.Glitch)
                {
                    iterations[index] = candidate[index];
                    resolved++;
                }
            }

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
        }

        return referencePasses;
    }

    private GlitchTile? FindWorstGlitchTile(int[] iterations, HashSet<int> excludedTiles)
    {
        int tileColumns = (width + TileSize - 1) / TileSize;
        int tileRows = (height + TileSize - 1) / TileSize;
        int[] counts = new int[tileColumns * tileRows];
        long[] sumX = new long[counts.Length];
        long[] sumY = new long[counts.Length];

        for (int index = 0; index < iterations.Length; index++)
        {
            if (iterations[index] != EscapeClassification.Glitch)
            {
                continue;
            }

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
        for (int index = 0; index < iterations.Length; index++)
        {
            if (iterations[index] != EscapeClassification.Glitch)
            {
                continue;
            }

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
        // MPFR repair is exact enough for the pixels perturbation cannot
        // classify, but it is CPU work. Scale the cap with image size so
        // larger renders get proportionally more repair headroom, then clamp
        // it by an iteration budget so deep views cannot stall indefinitely.
        int pixelCount = width * height;
        int resolutionLimit = (int)global::System.Math.Ceiling(pixelCount * MaxFinalRepairPixelFraction);
        int iterationWeightedLimit = (int)(TargetFinalRepairIterations / global::System.Math.Max(maxIterations, 1));
        int adaptiveLimit = global::System.Math.Min(resolutionLimit, iterationWeightedLimit);
        int boundedLimit = global::System.Math.Max(MinFinalRepairPixels, adaptiveLimit);

        return global::System.Math.Min(pixelCount, boundedLimit);
    }

    private int RepairGlitches(MandelbrotViewport viewport, int[] iterations, int maxIterations)
    {
        int[] glitchIndexes = iterations
            .Select((iteration, index) => iteration == EscapeClassification.Glitch ? index : -1)
            .Where(index => index >= 0)
            .ToArray();

        Parallel.ForEach(glitchIndexes, index =>
        {
            // Each repair is independent, so CPU parallelism is useful here.
            // The cap above keeps this from dominating normal interactive use.
            int x = index % width;
            int y = index / width;
            double normalizedX = (x + 0.5) / width;
            double normalizedY = (y + 0.5) / height;

            using MpfrComplex c = viewport.PointAt(normalizedX, normalizedY);
            iterations[index] = MpfrMandelbrot.EscapeIterations(c, maxIterations);
        });

        return glitchIndexes.Length;
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
