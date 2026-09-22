using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MandelbrotGpu;

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
