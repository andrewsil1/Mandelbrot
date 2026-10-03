using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

internal static class SliceChecks
{
    public static void Run()
    {
        using EnvironmentScope environment = new("MANDELBROT_SLICE_ITERATIONS",
            "MANDELBROT_ACCELERATION", "MANDELBROT_READBACK_SLICES");
        CheckFixtures();
        CheckBatchAndReferenceReuse();
        CheckReadbackSeams();
        CheckJournal();
        Console.WriteLine("Slice validation passed: FP64/DD state resume, BLA boundaries, sparse maps, batch/reference reuse, journal metadata.");
    }

    private static void CheckFixtures()
    {
        const int width = 11, height = 7;
        (string Name, double Real, double Imaginary, double Scale, int Budget, int[] Slices)[] fixtures =
        [
            ("escaping reference", 1, 0, 0.25, 257, [1, 7, 127, 128, 129]),
            ("tip boundary", -2, 0, 1E-13, 33, [1, 7, 32]),
            ("period-three", -1.7548776662466927, 0, 1E-20, 1025, [7, 127, 128, 129, 257]),
            ("filament", -0.743643887037151, 0.13182590420533, 1E-13, 4096, [127, 128, 129]),
            ("BLA escape", -0.743643887037151, 0.13182590420533, 1E-20, 4096, [127, 128, 129]),
            ("DD depth", -0.743643887037151, 0.13182590420533, 1E-28, 4096, [127, 128, 129])
        ];
        long resumedDispatches = 0;
        long skippedReadbacks = 0;
        foreach (var fixture in fixtures)
        {
            using MpfrComplex reference = new(MpfrFloat.FromDouble(fixture.Real, 384), MpfrFloat.FromDouble(fixture.Imaginary, 384));
            MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(reference, fixture.Scale);
            int[] expected = Enumerable.Range(0, width * height).Select(index =>
            {
                using MpfrComplex point = view.PointAtPixel(index % width, index / width, width, height);
                return MpfrMandelbrot.EscapeIterations(point, fixture.Budget, 768);
            }).ToArray();
            foreach (string acceleration in new[] { "none", "rebase", "bla" })
            foreach (bool dd in new[] { false, true })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", acceleration);
                Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "0");
                int[] baseline = Raw(width, height, view, reference, fixture.Budget, dd, out RenderTimings baselineTimings);
                Validate(baseline, expected, baseline, acceleration, "unsliced");
                foreach (int slice in fixture.Slices)
                {
                    Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", slice.ToString());
                    int[] actual = Raw(width, height, view, reference, fixture.Budget, dd, out RenderTimings timings);
                    Validate(actual, expected, baseline, acceleration, $"{fixture.Name}/{(dd ? "DD" : "FP64")}/{slice}");
                    if (acceleration != "bla" && (timings.ScalarIterations != baselineTimings.ScalarIterations
                        || timings.Rebases != baselineTimings.Rebases))
                        throw new Exception("Slice resume reset metrics or repeated work on finished pixels.");
                    resumedDispatches += Math.Max(0, timings.DispatchCount - 1);
                }
                // Slice length and GPU math stay identical. Unlike unsliced/BLA
                // comparisons, even raw glitch classifications must match exactly.
                Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "1");
                int[] perSlice = Raw(width, height, view, reference, fixture.Budget, dd, out RenderTimings perSliceTimings);
                foreach (int interval in new[] { 4, 8 })
                {
                    Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", interval.ToString());
                    int[] delayed = Raw(width, height, view, reference, fixture.Budget, dd, out RenderTimings delayedTimings);
                    CheckDelayed(perSlice, delayed, perSliceTimings, delayedTimings, interval, 1);
                    Validate(delayed, expected, perSlice, acceleration, $"{fixture.Name}/readback-{interval}");
                    skippedReadbacks += delayedTimings.SkippedSliceReadbacks;
                }
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", null);
            }
            Console.WriteLine($"Slice fixture {fixture.Name}: sliced/unsliced trusted counts match 768-bit MPFR.");
        }
        if (resumedDispatches == 0) throw new Exception("Slicing fixtures never resumed a pending pixel.");
        if (skippedReadbacks == 0) throw new Exception("Delayed-readback fixtures never skipped a copy.");
        Console.WriteLine($"Delayed FP64/DD readback validation passed: raw classifications and work counters unchanged, {skippedReadbacks:n0} copies skipped.");
    }

    private static int[] Raw(int width, int height, MandelbrotViewport view, MpfrComplex reference,
        int budget, bool dd, out RenderTimings timings)
    {
        MandelbrotRenderer renderer = new(width, height);
        int[] result;
        if (dd)
        {
            using PerturbationBuffers buffers = new(GraphicsDevice.GetDefault(), width * height, budget);
            result = renderer.RenderPerturbationDoubleDouble(view, reference, budget, Enumerable.Range(0, width * height).ToArray(), buffers);
        }
        else result = renderer.RenderPerturbationFloat64(view, reference, budget);
        timings = renderer.Timings;
        return result;
    }

    private static void Validate(int[] actual, int[] expected, int[] baseline, string acceleration, string context)
    {
        for (int index = 0; index < actual.Length; index++)
        {
            if (actual[index] < -2 || (actual[index] != -2 && actual[index] != expected[index]))
                throw new Exception($"{context}: pixel {index}, actual={actual[index]}, MPFR={expected[index]}.");
            // Without BLA, slicing changes no arithmetic operations: even glitch
            // classifications must be identical, including restored error estimates.
            if (acceleration != "bla" && actual[index] != baseline[index])
                throw new Exception($"{context}: exact slice/unsliced classification mismatch at {index}.");
            if (actual[index] != -2 && baseline[index] != -2 && actual[index] != baseline[index])
                throw new Exception($"{context}: trusted sliced/unsliced escape count differs.");
        }
    }

    private static void CheckBatchAndReferenceReuse()
    {
        // Cross the 32768-pixel DD batch boundary and reuse state across a new
        // reference and a short, unordered sparse prefix. No MPFR repair involved.
        const int width = 257, height = 129, budget = 257;
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "rebase");
        using MpfrComplex reference = new(MpfrFloat.FromDouble(-1.7548776662466927, 384), MpfrFloat.FromDouble(0, 384));
        MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(reference, 1E-20);
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "0");
        int[] baseline = Raw(width, height, view, reference, budget, true, out _);
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "7");
        Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "8");
        MandelbrotRenderer renderer = new(width, height);
        using PerturbationBuffers buffers = new(GraphicsDevice.GetDefault(), baseline.Length, budget);
        int stateLength = buffers.State.Length;
        if (stateLength != 32768 * 7) throw new Exception("DD state allocation is not bounded by batch size.");
        ReadBackBuffer<int> readback = buffers.Readback;
        if (readback.Length != 32768) throw new Exception("Readback staging is not batch-sized.");
        int[] full = renderer.RenderPerturbationDoubleDouble(view, reference, budget, Enumerable.Range(0, baseline.Length).ToArray(), buffers);
        if (!full.SequenceEqual(baseline)) throw new Exception("Resumed DD batch seam mismatch.");
        using MpfrComplex secondReference = view.PointAtPixel(32, 16, width, height);
        int[] sparse = Enumerable.Range(0, baseline.Length).Reverse().Take(137).ToArray();
        int[] actual = renderer.RenderPerturbationDoubleDouble(view, secondReference, budget, sparse, buffers);
        if (!ReferenceEquals(readback, buffers.Readback))
            throw new Exception("Sparse reference retry replaced the readback staging buffer.");
        for (int index = 0; index < sparse.Length; index++)
        {
            using MpfrComplex point = view.PointAtPixel(sparse[index] % width, sparse[index] / width, width, height);
            int expected = MpfrMandelbrot.EscapeIterations(point, budget, 768);
            if (actual[index] < -2 || (actual[index] != -2 && actual[index] != expected))
                throw new Exception("Sparse reference reuse failed MPFR comparison.");
        }
    }

    private static void CheckDelayed(int[] baseline, int[] actual, RenderTimings before, RenderTimings after,
        int interval, int batches)
    {
        if (!actual.SequenceEqual(baseline) || after.Rebases != before.Rebases
            || after.SkippedIterations != before.SkippedIterations || after.ScalarIterations != before.ScalarIterations)
            throw new Exception("Delayed readback changed raw results or repeated finished-pixel arithmetic.");
        if (after.DispatchCount < before.DispatchCount || after.DispatchCount > before.DispatchCount + (interval - 1) * batches
            || after.SliceReadbackCount > before.SliceReadbackCount
            || after.SliceReadbackCount + after.SkippedSliceReadbacks != after.DispatchCount)
            throw new Exception("Delayed readback exceeded terminal no-op bounds or copy accounting failed.");
    }

    private static void CheckReadbackSeams()
    {
        // Cross both FP64's 131072-pixel seam and DD's 32768-pixel seams.
        const int width = 515, height = 257, budget = 257;
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "rebase");
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "7");
        using MpfrComplex reference = new(MpfrFloat.FromDouble(-1.7548776662466927, 384), MpfrFloat.FromDouble(0, 384));
        MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(reference, 1E-20);
        foreach (bool dd in new[] { false, true })
        {
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "1");
            int[] baseline = Raw(width, height, view, reference, budget, dd, out RenderTimings before);
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "8");
            int[] delayed = Raw(width, height, view, reference, budget, dd, out RenderTimings after);
            int batchSize = dd ? 32768 : 131072;
            CheckDelayed(baseline, delayed, before, after, 8, (baseline.Length + batchSize - 1) / batchSize);
        }
        Console.WriteLine("Delayed readback FP64/DD batch seams passed; sparse reference reuse also uses interval 8.");
    }

    private static void CheckJournal()
    {
        string path = DispatchJournal.LogPath;
        bool resumed = false;
        foreach (string line in File.ReadLines(path))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement entry = document.RootElement;
            if (entry.GetProperty("phase").GetString() != "begin") continue;
            if (entry.GetProperty("adapter").GetString() is not { Length: > 0 }
                || entry.GetProperty("iterationBudget").GetInt32() <= 0
                || entry.GetProperty("referencePass").GetInt32() <= 0
                || entry.GetProperty("resources").ValueKind != JsonValueKind.Object)
                throw new Exception("Dispatch diagnostics are missing workload/adapter metadata.");
            if (entry.GetProperty("sliceStart").GetInt32() > 0) resumed = true;
        }
        if (!resumed) throw new Exception("Dispatch journal contains no resumed slices.");
    }
}
