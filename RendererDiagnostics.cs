using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MandelbrotGpu;

internal static class RendererDiagnostics
{
    // Ordinary Release rendering does not collect diagnostics. An explicit
    // opt-in lets regression/profiling tools exercise the optimized binary.
    public static bool Enabled => Environment.GetEnvironmentVariable("MANDELBROT_DIAGNOSTICS") switch
    {
        "0" => false,
        "1" => true,
        null => DefaultEnabled,
        _ => throw new ArgumentOutOfRangeException("MANDELBROT_DIAGNOSTICS")
    };

#if DEBUG
    private const bool DefaultEnabled = true;
#else
    private const bool DefaultEnabled = false;
#endif
}

// Disabled stage timers neither allocate Stopwatch objects nor query the clock.
// The separate dispatch-duration timer stays enabled for the safety brake.
internal struct DiagnosticTimer(bool enabled)
{
    private long started = enabled ? Stopwatch.GetTimestamp() : 0;
    public static DiagnosticTimer StartNew(bool enabled) => new(enabled);
    public TimeSpan Elapsed => enabled ? Stopwatch.GetElapsedTime(started) : TimeSpan.Zero;
    public void Restart()
    {
        if (enabled) started = Stopwatch.GetTimestamp();
    }
}

// GPU counters are explicit diagnostic opt-in, never quiet Release collection.
internal sealed class BlaPassProfile
{
    public const int CounterCount = 18;
    public const int MetricStride = 3 + CounterCount;
    public static readonly string[] CounterNames = ["searches", "candidates", "zeroRadius", "deltaRejected",
        "errorRejected", "acceptedBlocks", "sliceLimited", "alignmentStops", "referenceLimited",
        "budgetLimited", "blocks2", "blocks4", "blocks8", "blocks16", "blocks32", "blocks64", "blocks128Plus", "eligibleCandidates"];
    public int Pixels { get; init; }
    public double ViewportBound { get; init; }
    public int UsableNodes { get; init; }
    public int ReferenceLength { get; init; }
    public double MaxRadius { get; init; }
    public bool GpuCountersAvailable { get; init; }
    public long ScalarIterations { get; set; }
    public long SkippedIterations { get; set; }
    public long Rebases { get; set; }
    public long[] Counters { get; } = new long[CounterCount];

    public static bool Enabled => Environment.GetEnvironmentVariable("MANDELBROT_BLA_PROFILE") switch
    {
        null or "0" => false,
        "1" => true,
        _ => throw new ArgumentOutOfRangeException("MANDELBROT_BLA_PROFILE")
    };


}

// Explicit opt-in for actual UI runs. No work or file I/O in ordinary rendering.
internal sealed class ViewportTelemetry : IDisposable
{
    private readonly StreamWriter output;
    private readonly Process process = Process.GetCurrentProcess();
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly TimeSpan cpu;
    private readonly Guid id = Guid.NewGuid();
    private bool completed;

    public static ViewportTelemetry? Start(MandelbrotViewport view, int width, int height, int budget)
        => Environment.GetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG") is { Length: > 0 } path
            ? new(path, view, width, height, budget) : null;

    private ViewportTelemetry(string path, MandelbrotViewport view, int width, int height, int budget)
    {
        output = new StreamWriter(path, append: true) { AutoFlush = true };
        cpu = process.TotalProcessorTime;
        Write(new { phase = "started", id, utc = DateTime.UtcNow, width, height, budget,
            encoding = "exact sum of binary64 hex bit patterns", x = view.CenterX.ToExactBinary64Terms(),
            y = view.CenterY.ToExactBinary64Terms(), span = view.Height.ToExactBinary64Terms(),
            aspectBits = BitConverter.DoubleToUInt64Bits(view.Aspect).ToString("X16"), precisionBits = view.CenterX.PrecisionBits });
    }

    public void Complete(RenderResult result)
    {
        watch.Stop(); process.Refresh();
        Write(new { phase = "completed", id, utc = DateTime.UtcNow, rendererLatencyMs = watch.Elapsed.TotalMilliseconds,
            cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, privateBytes = process.PrivateMemorySize64,
            workingSet = process.WorkingSet64, processLifetimePeakWorkingSet = process.PeakWorkingSet64, handles = process.HandleCount,
            result.Float64GlitchCount, result.UsedDoubleDoubleFallback, mode = result.Mode.ToString(),
            result.ReferencePasses, result.RepairedCount, result.UnresolvedGlitchCount, result.FinalRepairLimit,
            result.Timings, result.Validation });
        completed = true;
    }

    private void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value));
    public void Dispose()
    {
        try { if (!completed) Write(new { phase = "failed", id, utc = DateTime.UtcNow }); }
        finally { output.Dispose(); process.Dispose(); }
    }
}
