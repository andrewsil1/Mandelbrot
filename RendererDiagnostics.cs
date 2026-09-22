using System.Diagnostics;

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
