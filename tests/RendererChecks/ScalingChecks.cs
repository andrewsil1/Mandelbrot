using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

internal static class ScalingChecks
{
    private const int Budget = 6912;
    private const int Slice = 128;
    private const double CenterReal = -0.34562335012588691;
    private const double CenterImaginary = 0.625450590999726;

    public static (int Width, int Height) Stage(string width) => width switch
    {
        "256" => (256, 130),
        "512" => (512, 260),
        "1024" => (1024, 520),
        "1920" => (1920, 980),
        "2880" => (2880, 1460),
        "3804" => (3804, 1932),
        _ => throw new ArgumentException("Stage must be 256, 512, 1024, 1920, 2880, or 3804.")
    };

    public static void CheckConfiguration()
    {
        foreach (string width in new[] { "256", "512", "1024", "1920", "2880", "3804" })
            if (Stage(width).Width != int.Parse(width)) throw new Exception("Scaling stage mapping failed.");
        if (Stage("2880").Height != 1460 || Stage("3804").Height != 1932)
            throw new Exception("Large scaling stage dimensions failed.");
        foreach (string invalid in new[] { "0", "-1", "3840", "4096", "invalid" })
        {
            try { Stage(invalid); throw new Exception("Unsupported scaling stage accepted."); }
            catch (ArgumentException) { }
        }
        RenderResult valid = new([], RenderMode.PerturbationDoubleDouble, 0, 0, 0, 1, 1024,
            new RenderTimings { DispatchCount = 1, MaxDispatchMilliseconds = 1 }, new(64, 0, 0));
        if (!Passed(valid) || Passed(valid with { Validation = null })
            || Passed(valid with { Validation = new(64, 1, 0) })
            || Passed(valid with { Validation = new(64, 0, 1) })
            || Passed(valid with { UnresolvedGlitchCount = 1 })
            || Passed(valid with { Timings = new() { DispatchCount = 1, MaxDispatchMilliseconds = 1000 } }))
            throw new Exception("Scaling failure/stop classification failed.");
        Console.WriteLine("Scaling stage configuration and numerical/dispatch stop checks passed (GPU-free).");
    }

    private static bool Passed(RenderResult result) =>
        result.Validation is { Samples: 64, Mismatches: 0, Unresolved: 0 }
        && result.UnresolvedGlitchCount == 0 && result.Timings.DispatchCount > 0
        && double.IsFinite(result.Timings.MaxDispatchMilliseconds)
        && result.Timings.MaxDispatchMilliseconds < 1000;

    public static void Run(string stage)
    {
        (int width, int height) = Stage(stage);
        // Explicit settings keep separate stage processes comparable. No metrics
        // buffer, automatic retries, or multi-stage loop. Larger stages must be
        // requested individually after reviewing the preceding stage's results.
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
        Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", Slice.ToString());
        Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", "8");
        Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
        int inFlight = (int)typeof(MandelbrotRenderer).Assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!
            .GetMethod("InFlightSubmissions")!.Invoke(null, null)!;

        string directory = Path.Combine(AppContext.BaseDirectory, "scaling-results");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, $"stage-{width}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        string journal = (string)typeof(MandelbrotRenderer).Assembly.GetType("MandelbrotGpu.DispatchJournal")!
            .GetProperty("LogPath")!.GetValue(null)!;
        Write(report, new { phase = "started", utc = DateTime.UtcNow, width, height,
            centerReal = CenterReal, centerImaginary = CenterImaginary, scale = Math.ScaleB(1, -40),
            iterationBudget = Budget, sliceIterations = Slice, journalGroupSlices = 8, readbackSlices = 4,
            journalCheckpointTargetMilliseconds = 100, inFlightSubmissions = inFlight,
            acceleration = "bla", metrics = false, journal });
        Console.WriteLine($"SCALING START: {width}x{height}, {Budget} iterations, {Slice}-iteration slices. No retries.");
        Console.WriteLine($"Results: {report}\nDispatch journal: {journal}");
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            GraphicsDevice device = GraphicsDevice.GetDefault();
            using MpfrComplex center = new(MpfrFloat.FromDouble(CenterReal, 384), MpfrFloat.FromDouble(CenterImaginary, 384));
            MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, Math.ScaleB(1, -40));
            RenderResult result = new MandelbrotRenderer(width, height).Render(view, Budget);
            timer.Stop();
            TimingChecks.CheckResult(result);
            JournalChecks.CheckLog();
            bool passed = Passed(result);
            Write(report, new { phase = "completed", utc = DateTime.UtcNow, passed, width, height,
                totalMilliseconds = timer.Elapsed.TotalMilliseconds, adapter = device.Name,
                adapterLuid = device.Luid.ToString(), mode = result.Mode.ToString(),
                result.ReferencePasses, result.InitialGlitchCount, result.RepairedCount,
                result.UnresolvedGlitchCount, result.FinalRepairLimit, result.Validation, result.Timings, journal });
            Console.WriteLine($"SCALING {(passed ? "PASS" : "FAIL")}: {width}x{height}, {timer.Elapsed.TotalSeconds:n2}s, " +
                $"{result.Mode}, refs={result.ReferencePasses}, repaired={result.RepairedCount}, unresolved={result.UnresolvedGlitchCount}, {result.Validation}");
            Console.WriteLine(result.Timings);
            if (!passed) throw new InvalidOperationException("Scaling validation failed. Stop: do not advance or automatically retry.");
        }
        catch (Exception ex)
        {
            // A kernel crash may prevent this record; a durable 'started' record
            // without completion must never be interpreted as a passing stage.
            Write(report, new { phase = "failed", utc = DateTime.UtcNow,
                elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, error = ex.ToString(), journal });
            throw;
        }
    }

    private static void Write(string path, object entry)
    {
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"));
        stream.Flush(flushToDisk: true);
    }
}
