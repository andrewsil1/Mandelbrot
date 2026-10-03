using ComputeSharp;
using MandelbrotGpu;

internal static class AccelerationChecks
{
    public static void Run()
    {
        using EnvironmentScope environment = new("MANDELBROT_ACCELERATION");
        const int width = 17, height = 9;
        long rebases = 0, skipped = 0;

        // Cover escaping references, tiny deltas near a filament, a periodic
        // component beyond the analytic shortcuts, and both precision ranges.
        (string Name, double Real, double Imaginary, double Scale, int Budget)[] fixtures =
        [
            ("escaping reference", 1, 0, 0.25, 256),
            ("DirectFloat transition", -0.67323438570448868, 0.35743485497289235, 9.095E-13, 6912),
            ("device-loss view", -0.34562335012588691, 0.625450590999726, 9.095E-13, 6912),
            // Rounded screenshot coordinates: cover this view and the next
            // click depth, without claiming the exact undisplayed MPFR center.
            ("suspended zoom transition", -0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -40), 6912),
            ("suspended zoom next click", -0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -42), 7232),
            ("tip boundary", -2, 0, 1E-13, 256),
            ("seahorse filament", -0.743643887037151, 0.13182590420533, 1E-13, 4096),
            ("BLA escape transition", -0.743643887037151, 0.13182590420533, 1E-20, 4096),
            ("period-three component", -1.7548776662466927, 0, 1E-20, 1024),
            ("DD depth", -0.743643887037151, 0.13182590420533, 1E-28, 4096),
            // Exactly representable preperiodic boundary: unlike a rounded
            // filament center, these views retain structure at extreme depths.
            ("preperiodic boundary 28", 0, 1, 1E-28, 4096),
            ("preperiodic boundary 60", 0, 1, 1E-60, 4096)
        ];

        foreach (var fixture in fixtures)
        {
            using MpfrComplex center = new(MpfrFloat.FromDouble(fixture.Real, 384), MpfrFloat.FromDouble(fixture.Imaginary, 384));
            MandelbrotViewport viewport = MandelbrotViewport.FullSet(width, height).Zoom(center, fixture.Scale);
            int[] expected = new int[width * height];
            for (int index = 0; index < expected.Length; index++)
            {
                using MpfrComplex point = viewport.PointAtPixel(index % width, index / width, width, height);
                expected[index] = MpfrMandelbrot.EscapeIterations(point, fixture.Budget, 768);
            }

            foreach (bool doubleDouble in new[] { false, true })
            {
                // Force each shader directly; automatic DD fallback or CPU
                // repair must not hide a mismatch in a raw trusted result.
                int[]? rebased = null;
                long scalarBaseline = 0;
                int unresolvedBaseline = 0;
                foreach (string mode in new[] { "none", "rebase", "bla" })
                {
                    Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", mode);
                    MandelbrotRenderer renderer = new(width, height);
                    using PerturbationBuffers buffers = new(GraphicsDevice.GetDefault(), expected.Length, fixture.Budget);
                    int[] actual = doubleDouble
                        ? renderer.RenderPerturbationDoubleDouble(viewport, center, fixture.Budget, Enumerable.Range(0, expected.Length).ToArray(), buffers)
                        : renderer.RenderPerturbationFloat64(viewport, center, fixture.Budget);
                    RenderTimings metrics = renderer.Timings;
                    int failures = actual.Where((value, index) => value != -2 && value != expected[index]).Count();
                    int unresolved = actual.Count(value => value == -2);
                    if (failures != 0)
                    {
                        foreach (int index in Enumerable.Range(0, actual.Length).Where(index => actual[index] != -2 && actual[index] != expected[index]).Take(10))
                            Console.WriteLine($"Mismatch pixel={index}: actual={actual[index]}, expected={expected[index]}");
                        throw new Exception($"{fixture.Name}/{(doubleDouble ? "DD" : "FP64")}/{mode}: {failures} MPFR mismatches, {unresolved} unresolved.");
                    }
                    if (mode == "rebase")
                    {
                        rebased = actual;
                        scalarBaseline = metrics.ScalarIterations;
                        unresolvedBaseline = unresolved;
                        rebases += metrics.Rebases;
                    }
                    if (mode == "bla")
                    {
                        // Exact count equality where both paths trust results.
                        if (actual.Where((value, index) => value != -2 && rebased![index] != -2 && value != rebased[index]).Any())
                            throw new Exception("BLA/rebased escape counts differ.");
                        skipped += metrics.SkippedIterations;
                        if (unresolved > unresolvedBaseline)
                            throw new Exception("BLA increased the unresolved count relative to rebasing alone.");
                        if (metrics.SkippedIterations > 0 && metrics.ScalarIterations >= scalarBaseline)
                            throw new Exception("BLA exercised skips but failed to reduce scalar work.");
                    }
                    Console.WriteLine($"{fixture.Name}/{(doubleDouble ? "DD" : "FP64")}/{mode}: MPFR mismatches=0, unresolved={unresolved}, rebase={metrics.Rebases}, skip={metrics.SkippedIterations}, scalar={metrics.ScalarIterations}");
                }
            }
            Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
            RenderResult pipeline = new MandelbrotRenderer(width, height).Render(viewport, fixture.Budget);
            TimingChecks.CheckResult(pipeline);
            if (pipeline.Validation is not { Mismatches: 0, Unresolved: 0 } || pipeline.UnresolvedGlitchCount != 0)
                throw new Exception($"{fixture.Name}: full rendering/repair pipeline validation failed.");
            if (fixture.Name.StartsWith("preperiodic boundary")
                && (pipeline.Mode != RenderMode.PerturbationFloat64
                    || pipeline.Timings.DoubleDoubleDispatchedThreads != 0))
                throw new Exception("Deep boundary fixture unnecessarily escalated the whole frame to DD.");
            if (pipeline.RepairedCount == 0 && pipeline.Timings.RepairMilliseconds != 0)
                throw new Exception("An empty repair tail performed CPU repair work.");
            Console.WriteLine($"{fixture.Name}/pipeline: validated, repaired={pipeline.RepairedCount}, unresolved=0");
        }
        if (rebases == 0 || skipped == 0) throw new Exception("Tests did not exercise rebasing and BLA.");
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", null);
        CheckTransitionFallback();
        Console.WriteLine($"Acceleration validation passed: {rebases:n0} rebases and {skipped:n0} skipped iterations exercised.");
    }

    public static void CheckTransitionFallback(int width = 256, int height = 130)
    {
        const int budget = 6912;
        using MpfrComplex center = new(MpfrFloat.FromDouble(-0.67323438570448868, 384), MpfrFloat.FromDouble(0.35743485497289235, 384));
        MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, Math.ScaleB(1, -40));
        int[] raw = new MandelbrotRenderer(width, height).RenderPerturbationFloat64(view, center, budget);
        int glitches = raw.Count(value => value == -2);
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        RenderResult result = new MandelbrotRenderer(width, height).Render(view, budget);
        TimingChecks.CheckResult(result);
        bool fallback = glitches > Math.Min(result.FinalRepairLimit,
            Math.Min(4096, Math.Max(1024, (int)Math.Ceiling((long)width * height * 0.005))));
        if (width == 256 && height == 130 && !result.UsedDoubleDoubleFallback)
            throw new Exception("Transition fixture bypassed sparse DD in favor of expensive CPU repair.");
        int expectedPasses = fallback ? 2 : 1;
        long expectedEvaluations = width * height + (fallback ? glitches : 0);
        if (result.ReferencePasses != expectedPasses || result.Timings.PerturbationPixelEvaluations != expectedEvaluations)
            throw new Exception("Transition fallback repeated a full frame or unnecessary reference retries.");
        if (result.Validation is not { Mismatches: 0, Unresolved: 0 } || result.UnresolvedGlitchCount != 0)
            throw new Exception($"Transition sparse fallback failed MPFR validation: {result.Validation}, unresolved={result.UnresolvedGlitchCount}, FP glitches={glitches}, mode={result.Mode}.");
        Console.WriteLine($"Transition fallback passed: {width}x{height}, elapsed={timer.Elapsed.TotalSeconds:n2}s, refs={result.ReferencePasses}, FP64 pixels={width * height}, DD pixels={glitches}, repaired={result.RepairedCount}; {result.Timings}");
    }
}
