namespace MandelbrotGpu;

// The API's group-count limit is much larger than a practical watchdog-safe
// workload. Bound both pixel count and worst-case iterations per submission.
// These are conservative work limits, not a guarantee of runtime on every GPU.
internal static class GpuDispatchPolicy
{
    // One retains synchronous execution for comparison. Two overlaps host
    // recording with preceding GPU work without creating an unbounded backlog.
    public static int InFlightSubmissions()
    {
        string? configured = Environment.GetEnvironmentVariable("MANDELBROT_INFLIGHT");
        if (configured is null) return 2;
        if (!int.TryParse(configured, out int value) || value < 1 || value > 2)
            throw new ArgumentOutOfRangeException("MANDELBROT_INFLIGHT");
        return value;
    }

    public static int ReadbackSlices()
    {
        string? configured = Environment.GetEnvironmentVariable("MANDELBROT_READBACK_SLICES");
        if (configured is null) return 4;
        if (!int.TryParse(configured, out int value) || value < 1 || value > 8)
            throw new ArgumentOutOfRangeException("MANDELBROT_READBACK_SLICES");
        return value;
    }

    // Applying a short double-double affine block can cost more than evaluating
    // the same iterations exactly. Keep this configurable so profiling can find
    // the profitable crossover without changing the table construction.
    public static int BlaMinimumBlockLength()
    {
        string? configured = Environment.GetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK");
        if (configured is null) return 4;
        if (!int.TryParse(configured, out int value) || value is not (2 or 4 or 8 or 16))
            throw new ArgumentOutOfRangeException("MANDELBROT_BLA_MIN_BLOCK");
        return value;
    }

    public static bool ShouldReadSlice(int completedSlices, int interval, bool finalSlice) =>
        completedSlices == 1 || completedSlices % interval == 0 || finalSlice;

    public static int SliceIterations(int maxIterations)
    {
        string? configured = Environment.GetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS");
        if (configured is null) return Math.Min(128, maxIterations);
        if (!int.TryParse(configured, out int value) || value < 0 || value > 32768)
            throw new ArgumentOutOfRangeException("MANDELBROT_SLICE_ITERATIONS");
        // Zero is an explicit unsliced comparison mode for small regression fixtures.
        return value == 0 ? maxIterations : Math.Min(value, maxIterations);
    }

    public static int BatchPixels(RenderMode mode, int maxIterations)
    {
        (int pixelLimit, long iterationLimit) = mode switch
        {
            RenderMode.DirectFloat64 => (65_536, 256_000_000),
            RenderMode.PerturbationFloat64 => (32_768, 128_000_000),
            RenderMode.PerturbationDoubleDouble => (8_192, 16_000_000),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        int count = (int)Math.Min(pixelLimit, iterationLimit / Math.Max(1, maxIterations));
        return Math.Max(64, count / 64 * 64);
    }
}
