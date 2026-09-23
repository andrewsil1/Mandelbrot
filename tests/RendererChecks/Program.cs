using System.Reflection;
using ComputeSharp;
using MandelbrotGpu;

try
{
    if (args.Length > 0 && args[0] == "--mpfr-temporaries")
    {
        if (args.Length != 2) throw new ArgumentException("Usage: --mpfr-temporaries OUTPUT.jsonl");
        MpfrTemporaryChecks.Profile(args[1]);
        return;
    }
    if (args.Length > 0 && args[0] == "--fma-production-smoke")
    {
        if (args.Length != 4) throw new ArgumentException("Usage: --fma-production-smoke LIVE_FRAMES.jsonl PRIOR_REPORT.jsonl OUTPUT.jsonl");
        FmaProductionChecks.Run(args[1], args[2], args[3]);
        return;
    }
    ProductionChecks.CheckConfiguration();
    // Test/profiling executables explicitly opt in, including Release tests.
    // ProductionChecks also exercises diagnostics-off rendering below.
    Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
    Environment.SetEnvironmentVariable("MANDELBROT_LOG_DIRECTORY", Path.Combine(AppContext.BaseDirectory, "dispatch-logs"));
    SafetyChecks.Run();
    ScalingChecks.CheckConfiguration();
    TimingChecks.CheckConfiguration();
    JournalChecks.Run();
    QueueChecks.CheckConfiguration();
    BlaProfilingChecks.CheckConfiguration();
    SuspendedUiChecks.Run();
    FenceWaitChecks.Run();
    if (args.Contains("--safety-only")) return;
    ProgressiveChecks.Run();
    DoubleDoubleArithmeticChecks.Run();
    RepairBudgetChecks.Run();
    MpfrTemporaryChecks.Run();
    SparseRetryChecks.Run();
    if (args.Contains("--fp32-profile"))
    {
        if (args.Length != 4) throw new ArgumentException("Usage: --fp32-profile WIDTH LIVE_FRAMES.jsonl OUTPUT.jsonl");
        var (w, h) = ScalingChecks.Stage(args[1]);
        Fp32ExperimentChecks.Profile(w, h, args[2], args[3]);
        return;
    }
    if (args.Contains("--cost-profile") || args.Contains("--responsiveness-profile"))
    {
        if (args.Length != 4) throw new ArgumentException("Usage: --responsiveness-profile WIDTH LIVE_FRAMES.jsonl OUTPUT.jsonl");
        var (w, h) = ScalingChecks.Stage(args[1]);
        ResponsivenessChecks.Profile(w, h, args[2], args[3], args[0] == "--cost-profile");
        return;
    }
    if (args.Contains("--interactive-stage"))
    {
        if (args.Length != 3) throw new ArgumentException("Usage: --interactive-stage WIDTH REPORT.jsonl");
        var (w, h) = ScalingChecks.Stage(args[1]);
        InteractiveChecks.Run(w, h, args[2]);
        return;
    }
    if (args.Contains("--precision-profile"))
    {
        if (args.Length != 3 || args[0] != "--precision-profile")
            throw new ArgumentException("Usage: --precision-profile WIDTH REPORT.jsonl.");
        (int w, int h) = ScalingChecks.Stage(args[1]);
        if (w > 1024) throw new ArgumentOutOfRangeException(nameof(w));
        PrecisionProfilingChecks.Run(w, h, args[2]);
        return;
    }
    if (args.Contains("--bla-sparse-profile"))
    {
        if (args.Length != 3 || args[0] != "--bla-sparse-profile")
            throw new ArgumentException("Usage: --bla-sparse-profile WIDTH REPORT.jsonl.");
        (int w, int h) = ScalingChecks.Stage(args[1]);
        if (w > 1920) throw new ArgumentOutOfRangeException(nameof(w));
        BlaProfilingChecks.RunSparse(w, h, args[2]);
        return;
    }
    if (args.Contains("--bla-profile"))
    {
        if (args.Length != 4 || args[0] != "--bla-profile")
            throw new ArgumentException("Usage: --bla-profile WIDTH FIXTURE REPORT.jsonl.");
        (int w, int h) = ScalingChecks.Stage(args[1]);
        if (w > 1920) throw new ArgumentOutOfRangeException(nameof(w));
        BlaProfilingChecks.Run(w, h, args[2], args[3]);
        return;
    }
    if (args.Contains("--production-only"))
    {
        ProductionChecks.CheckRendering();
        return;
    }
    if (args.Contains("--production-stage"))
    {
        if (args.Length < 2 || args.Length > 6 || args[0] != "--production-stage")
            throw new ArgumentException("Usage: --production-stage WIDTH [QUIET_FRAMES=1..5] [PROGRESS_PATH] [STOP_PATH] [FIXTURE=original].");
        (int stageWidth, int stageHeight) = ScalingChecks.Stage(args[1]);
        int frames = args.Length >= 3 ? int.Parse(args[2]) : 1;
        if (frames < 1 || frames > 5) throw new ArgumentOutOfRangeException(nameof(frames));
        ProductionChecks.CheckRendering(stageWidth, stageHeight, largeStage: true, quietFrames: frames,
            progressPath: args.Length >= 4 ? args[3] : null, stopPath: args.Length >= 5 ? args[4] : null,
            fixture: args.Length >= 6 ? args[5] : "original");
        return;
    }
    if (args.Contains("--scale-stage"))
    {
        if (args.Length != 2 || args[0] != "--scale-stage")
            throw new ArgumentException("Usage: --scale-stage 256|512|1024|1920|2880|3804 (one stage per process).");
        ScalingChecks.Run(args[1]);
        return;
    }
    if (args.Contains("--slice-only"))
    {
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");
        SliceChecks.Run();
        return;
    }

    if (args.Contains("--transition-4k"))
    {
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", null);
        Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
        AccelerationChecks.CheckTransitionFallback(3804, 1932);
        return;
    }

    if (args.Contains("--device-loss-4k"))
    {
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", null);
        Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
        using MpfrComplex center = new(MpfrFloat.FromDouble(-0.34562335012588691, 384), MpfrFloat.FromDouble(0.625450590999726, 384));
        MandelbrotViewport view = MandelbrotViewport.FullSet(3804, 1932).Zoom(center, Math.ScaleB(1, -40));
        RenderResult result = new MandelbrotRenderer(3804, 1932).Render(view, 6912);
        if (result.Validation is not { Mismatches: 0, Unresolved: 0 } || result.UnresolvedGlitchCount != 0)
            throw new Exception($"Device-loss view validation failed: {result.Validation}, unresolved={result.UnresolvedGlitchCount}");
        Console.WriteLine($"Device-loss 4K view completed: refs={result.ReferencePasses}, repaired={result.RepairedCount}, unresolved=0; {result.Timings}");
        return;
    }

    Fp32ExperimentChecks.Run();
    TimingChecks.CheckQuietAttribution();
    Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");

    const int width = 67;
    const int height = 35;
    const int budget = 256;
    Assembly assembly = typeof(MandelbrotRenderer).Assembly;
    Type bufferType = assembly.GetType("MandelbrotGpu.PerturbationBuffers")!;
    MethodInfo dispatch = typeof(MandelbrotRenderer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderPerturbationDoubleDouble" && method.GetParameters().Length == 5);

    MandelbrotViewport viewport = MandelbrotViewport.FullSet(width, height);
    MandelbrotRenderer renderer = new(width, height);
    using MpfrComplex reference = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
    using IDisposable buffers = (IDisposable)Activator.CreateInstance(bufferType, GraphicsDevice.GetDefault(), width * height, budget)!;
    int[] fullIndices = Enumerable.Range(0, width * height).ToArray();
    int[] Full(int[] indices) => (int[])dispatch.Invoke(renderer, [viewport, reference, budget, indices, buffers])!;
    int[] full = Full(fullIndices);

    // Unordered indices and non-threadgroup-sized tails exercise mapping, partial
    // transfers, buffer reuse, and generated dispatch bounds checks.
    foreach (int count in new[] { 1, 7, 65, 137 })
    {
        int[] indices = fullIndices.Reverse().Where(index => index % 3 == 0).Take(count).ToArray();
        int[] sparse = Full(indices);
        if (!sparse.SequenceEqual(indices.Select(index => full[index])))
            throw new Exception($"Sparse/full mismatch at count {count}.");
    }
    Console.WriteLine("Sparse/full equivalence passed (1, 7, 65, 137 pixels; reused buffers).");

    int[] seeded = (int[])full.Clone();
    int[] seededIndices = Enumerable.Range(30, 10).Select(x => (height / 2) * width + x).ToArray();
    int glitch = (int)assembly.GetType("MandelbrotGpu.EscapeClassification")!.GetField("Glitch")!.GetRawConstantValue()!;
    foreach (int index in seededIndices) seeded[index] = glitch;
    MethodInfo retries = typeof(MandelbrotRenderer).GetMethod("AddExtraReferencePasses", BindingFlags.Instance | BindingFlags.NonPublic)!;
    RenderTimings timings = (RenderTimings)typeof(MandelbrotRenderer)
        .GetField("timings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
    long before = timings.PerturbationPixelEvaluations;
    int passes = (int)retries.Invoke(renderer, [viewport, seeded, budget, buffers])!;
    if (passes != 0 || timings.PerturbationPixelEvaluations != before)
        throw new Exception("Reference retries ran despite the tail fitting the repair budget.");
    MethodInfo merge = typeof(MandelbrotRenderer).GetMethod("MergeResolved", BindingFlags.Static | BindingFlags.NonPublic)!;
    int resolved = (int)merge.Invoke(null, [seeded, seededIndices, seededIndices.Select(index => full[index]).ToArray()])!;
    if (resolved != seededIndices.Length || !seeded.SequenceEqual(full))
        throw new Exception("Sparse merge changed trusted pixels or failed to resolve the tail.");
    Console.WriteLine("Retry budget and sparse merge passed: no unnecessary retries, trusted pixels preserved.");

    Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
    foreach (double scale in new[] { 1.0, 1E-13, 1E-27 })
    {
        using MpfrComplex center = new(MpfrFloat.FromDouble(-0.125, 384), MpfrFloat.FromDouble(0.75, 384));
        MandelbrotViewport testView = scale == 1.0 ? viewport : viewport.Zoom(center, scale);
        RenderResult result = renderer.Render(testView, budget);
        TimingChecks.CheckResult(result);
        Console.WriteLine($"{result.Mode}: {result.Validation}; {result.Timings}");
        if (result.Validation is not { Mismatches: 0, Unresolved: 0 })
            throw new Exception("MPFR sample validation failed.");
    }

    // Check the DD pixel-center mapping directly before repairs can conceal errors.
    Type validationType = assembly.GetType("MandelbrotGpu.NumericalValidation")!;
    RenderValidation validation = (RenderValidation)validationType.GetMethod("Check")!
        .Invoke(null, [viewport, full, width, height, budget])!;
    if (validation.Mismatches != 0 || validation.Unresolved != 0)
        throw new Exception($"Raw double-double validation failed: {validation}");
    Console.WriteLine("Raw double-double pixel-center validation passed.");

    // A 4K frame exceeds the one-axis group limit. Poison output first so an
    // omitted batch or incorrect offset cannot pass by returning default values.
    foreach (int count in new[] { 8191, 8192, 8193, 65_535 * 64 - 1, 65_535 * 64, 65_535 * 64 + 1, 3804 * 1932 })
    {
        const int largeWidth = 3804;
        MandelbrotViewport largeView = MandelbrotViewport.FullSet(largeWidth, 1932);
        MandelbrotRenderer largeRenderer = new(largeWidth, 1932);
        using MpfrComplex largeReference = new(largeView.CenterX.Clone(), largeView.CenterY.Clone());
        using IDisposable largeBuffers = (IDisposable)Activator.CreateInstance(bufferType, GraphicsDevice.GetDefault(), count, 1)!;
        ReadWriteBuffer<int> output = (ReadWriteBuffer<int>)bufferType.GetProperty("Output")!.GetValue(largeBuffers)!;
        output.CopyFrom(Enumerable.Repeat(int.MinValue, count).ToArray());
        int[] indices = Enumerable.Range(0, count).Reverse().ToArray();
        int[] result = (int[])dispatch.Invoke(largeRenderer, [largeView, largeReference, 1, indices, largeBuffers])!;
        // With one iteration, z_0 is zero and every pixel reaches the budget.
        if (result.Any(value => value != -1))
            throw new Exception($"Batched dispatch failed at {count} pixels.");
        Console.WriteLine($"Batched dispatch passed: {count:n0} pixels, every output written.");
    }

    AccelerationChecks.Run();
    BlaProfilingChecks.CheckRendering();
    DispatchChecks.Run();
    SliceChecks.Run();
    JournalChecks.CheckEquivalence();
    QueueChecks.CheckRendering();
    ProductionChecks.CheckRendering();
    FenceWaitChecks.CheckRendererHandles();
    JournalChecks.CheckLog();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Renderer regression FAILED:\n{ex}");
    Environment.ExitCode = 1;
}
