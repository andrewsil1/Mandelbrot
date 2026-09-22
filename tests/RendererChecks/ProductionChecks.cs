using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

internal static class ProductionChecks
{
    private static readonly Assembly Assembly = typeof(MandelbrotRenderer).Assembly;
    private static bool Enabled => (bool)Assembly.GetType("MandelbrotGpu.RendererDiagnostics")!.GetProperty("Enabled")!.GetValue(null)!;

    public static (double Real, double Imaginary, double Scale, int Budget) Fixture(string name) => name switch
    {
        "original" => (-0.34562335012588691, 0.625450590999726, Math.ScaleB(1, -40), 6912),
        // Only the rounded coordinates in the screenshot are available.
        "suspended-zoom" => (-0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -40), 6912),
        "suspended-zoom-next" => (-0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -42), 7232),
        _ => throw new ArgumentException("Unknown production fixture.", nameof(name))
    };

    public static void CheckConfiguration()
    {
        if (Fixture("suspended-zoom-next").Scale != Fixture("suspended-zoom").Scale * 0.25)
            throw new Exception("Suspended zoom fixture step failed.");
        try { _ = Fixture("invalid"); throw new Exception("Unknown fixture accepted."); }
        catch (ArgumentException) { }
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_DIAGNOSTICS");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", null);
#if DEBUG
            const bool expected = true;
#else
            const bool expected = false;
#endif
            if (Enabled != expected) throw new Exception("Configuration diagnostic default is incorrect.");
            foreach (string value in new[] { "0", "1" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", value);
                if (Enabled != (value == "1")) throw new Exception("Diagnostic override failed.");
            }
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "invalid");
            try { _ = Enabled; throw new Exception("Invalid diagnostic override accepted."); }
            catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", previous); }
        Console.WriteLine("Diagnostic policy passed: Debug on, Release off, explicit profiling override (GPU-free).");
    }

    public static void CheckRendering(int width = 13, int height = 9, bool largeStage = false,
        int quietFrames = 1, string? progressPath = null, string? stopPath = null, string fixture = "original")
    {
        if (quietFrames < 1 || quietFrames > 5) throw new ArgumentOutOfRangeException(nameof(quietFrames));
        var selected = Fixture(fixture);
        if (!largeStage && fixture != "original") throw new ArgumentException("Select a stage for alternate fixtures.");
        string[] names = ["MANDELBROT_DIAGNOSTICS", "MANDELBROT_METRICS", "MANDELBROT_VALIDATE", "MANDELBROT_INFLIGHT", "MANDELBROT_SLICE_ITERATIONS",
            "MANDELBROT_ACCELERATION", "MANDELBROT_READBACK_SLICES"];
        string?[] previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        string journal = (string)Assembly.GetType("MandelbrotGpu.DispatchJournal")!.GetProperty("LogPath")!.GetValue(null)!;
        if (largeStage)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "scaling-results");
            Directory.CreateDirectory(directory);
            progressPath ??= Path.Combine(directory, $"production-progress-{width}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        }
        try
        {
            Progress(progressPath, new
            {
                phase = "started",
                utc = DateTime.UtcNow,
                width,
                height,
                quietFrames,
                iterationBudget = selected.Budget,
                fixture,
                sliceIterations = 128,
                inFlightSubmissions = 2,
                readbackSlices = 4,
                acceleration = "bla",
                pid = Environment.ProcessId
            });
            CheckStop(stopPath);
            Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
            Environment.SetEnvironmentVariable("MANDELBROT_METRICS", largeStage ? null : "1");
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
            Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
            double[] scales = largeStage ? [selected.Scale] : [1, Math.ScaleB(1, -40), 1E-28];
            foreach (string depth in (largeStage ? new[] { "2" } : new[] { "1", "2" }))
                foreach (double scale in scales)
                {
                    Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", depth);
                    using MpfrComplex center = new(MpfrFloat.FromDouble(selected.Real, 384), MpfrFloat.FromDouble(selected.Imaginary, 384));
                    // Reuse the existing DirectFloat MPFR fixture; a translated,
                    // high-iteration coarse grid can sample numerically sensitive
                    // boundaries unrelated to enabling/disabling instrumentation.
                    int renderWidth = scale == 1 ? 67 : width;
                    int renderHeight = scale == 1 ? 35 : height;
                    int budget = scale == 1 ? 256 : selected.Budget;
                    MandelbrotViewport view = scale == 1
                        ? MandelbrotViewport.FullSet(renderWidth, renderHeight)
                        : MandelbrotViewport.FullSet(renderWidth, renderHeight).Zoom(center, scale);
                    // Keep explicit DD coverage after removing depth-only DD
                    // selection. Large production stages use the shipping policy.
                    RenderResult RenderSelected(MandelbrotRenderer renderer) => !largeStage && scale < 1E-26
                        ? PrecisionProfilingChecks.Render(renderer, view, budget, true)
                        : renderer.Render(view, budget);
                    Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
                    Progress(progressPath, new { phase = "baseline-started", utc = DateTime.UtcNow, scale });
                    Stopwatch timer = Stopwatch.StartNew();
                    RenderResult baseline = RenderSelected(new(renderWidth, renderHeight));
                    double baselineMs = timer.Elapsed.TotalMilliseconds;
                    if (baseline.Validation is not { Mismatches: 0, Unresolved: 0 })
                        throw new Exception($"Production baseline failed MPFR: mode={baseline.Mode}, scale={scale}, depth={depth}, {baseline.Validation}.");
                    if (baseline.UnresolvedGlitchCount != 0) throw new Exception("Production baseline has unresolved pixels.");
                    Progress(progressPath, new
                    {
                        phase = "baseline-completed",
                        utc = DateTime.UtcNow,
                        baselineMs,
                        baseline.Validation,
                        mode = baseline.Mode.ToString(),
                        baseline.RepairedCount,
                        baseline.UnresolvedGlitchCount,
                        baseline.ReferencePasses,
                        baseline.Timings.DoubleDoubleDispatchedThreads,
                        imageSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Runtime.InteropServices.MemoryMarshal.AsBytes(baseline.Pixels.AsSpan()))),
                        adapter = GraphicsDevice.GetDefault().Name,
                        adapterLuid = GraphicsDevice.GetDefault().Luid.ToString()
                    });
#if DEBUG
                    Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
#else
                // Exercise the shipped default, not only an explicit override.
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", null);
#endif
                    long length = new FileInfo(journal).Length;
                    RenderResult production;
                    double productionMs;
                    List<double> frameMilliseconds = [];
                    // Reuse the renderer/device across quiet frames to exercise repeated
                    // frame allocation, completion draining, and resource release.
                    MandelbrotRenderer renderer = new(renderWidth, renderHeight);
                    for (int frame = 1; frame <= quietFrames; frame++)
                    {
                        CheckStop(stopPath);
                        Progress(progressPath, new { phase = "frame-started", utc = DateTime.UtcNow, frame, diagnostics = Enabled });
                        // An attempted automatic write now fails: ordinary rendering
                        // must succeed even when the diagnostic log is unwritable.
                        using (FileStream locked = new(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            timer.Restart();
                            production = RenderSelected(renderer);
                            productionMs = timer.Elapsed.TotalMilliseconds;
                            if (locked.Length != length) throw new Exception("Diagnostics-off rendering changed its log.");
                        }
                        if (!baseline.Pixels.SequenceEqual(production.Pixels) || baseline.Mode != production.Mode
                            || baseline.ReferencePasses != production.ReferencePasses || baseline.RepairedCount != production.RepairedCount
                            || baseline.UnresolvedGlitchCount != production.UnresolvedGlitchCount || production.Validation is not null
                            || typeof(RenderTimings).GetProperties().Where(p => p.PropertyType == typeof(double) || p.PropertyType == typeof(int) || p.PropertyType == typeof(long))
                                .Any(p => Convert.ToDouble(p.GetValue(production.Timings)) != 0))
                            throw new Exception("Diagnostics-off rendering changed output or still collected instrumentation.");
                        using IDisposable buffers = (IDisposable)Activator.CreateInstance(Assembly.GetType("MandelbrotGpu.PerturbationBuffers")!, GraphicsDevice.GetDefault(), 1, 1)!;
                        if ((bool)buffers.GetType().GetProperty("MetricsEnabled")!.GetValue(buffers)!)
                            throw new Exception("Production enabled the GPU diagnostic metrics buffer.");
                        frameMilliseconds.Add(productionMs);
                        Progress(progressPath, new
                        {
                            phase = "frame-completed",
                            utc = DateTime.UtcNow,
                            frame,
                            productionMs,
                            exactImage = true,
                            production.RepairedCount,
                            production.UnresolvedGlitchCount,
                            journalBytes = new FileInfo(journal).Length
                        });
                        Console.WriteLine($"QUIET FRAME {frame}/{quietFrames}: {width}x{height}, {productionMs:n1}ms; exact image, no log writes.");
                        CheckStop(stopPath);
                        if (largeStage)
                        {
                            string directory = Path.Combine(AppContext.BaseDirectory, "scaling-results");
                            Directory.CreateDirectory(directory);
                            string path = Path.Combine(directory, $"production-{width}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");
                            File.WriteAllText(path, JsonSerializer.Serialize(new
                            {
                                passed = true,
                                width,
                                height,
                                fixture,
                                scale,
                                baselineMilliseconds = baselineMs,
                                productionMilliseconds = productionMs,
                                quietFramesCompleted = frame,
                                frameMilliseconds,
                                baseline.Validation,
                                production.RepairedCount,
                                production.UnresolvedGlitchCount,
                                journalBytesBefore = length,
                                journalBytesAfter = new FileInfo(journal).Length,
                                comparison = "Exact whole-image comparison; same process, baseline first, warm-cache production. No general speed assertion."
                            }));
                            Console.WriteLine($"PRODUCTION PASS: {width}x{height}, diagnostics={baselineMs:n1}ms, production={productionMs:n1}ms; exact image, no log writes. Report: {path}");
                        }
                    }
                }
            Progress(progressPath, new { phase = "completed", utc = DateTime.UtcNow, passed = true, quietFrames });
        }
        catch (Exception ex)
        {
            Progress(progressPath, new { phase = "failed", utc = DateTime.UtcNow, error = ex.ToString() });
            throw;
        }
        finally { for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]); }
        Console.WriteLine(largeStage
            ? $"Production stage passed: {width}x{height}, {quietFrames} quiet frame(s), exact baseline images and zero unresolved pixels."
            : "Diagnostics-off production validation passed: exact Direct/FP64/DD images, both queue policies, no logging, timers, counters, metrics or sampling.");
    }

    private static void CheckStop(string? path)
    {
        if (path is not null && File.Exists(path))
            throw new InvalidOperationException("External health monitor requested a stop. No more frames will be submitted.");
    }

    private static void Progress(string? path, object entry)
    {
        if (path is null) return;
        // Frame boundaries only, in the test executable. No renderer hot-path
        // instrumentation; a reboot leaves a durable last-started frame.
        using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"));
        stream.Flush(flushToDisk: true);
    }
}
