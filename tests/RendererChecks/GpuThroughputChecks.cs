using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MandelbrotGpu;

internal static class GpuThroughputChecks
{
    public static void Profile(int width, int height, string fixture, string output)
    {
        if (File.Exists(output)) throw new IOException("Refusing to overwrite GPU profile.");
        string[] names = ["MANDELBROT_DIAGNOSTICS", "MANDELBROT_METRICS", "MANDELBROT_VALIDATE",
            "MANDELBROT_SLICE_ITERATIONS", "MANDELBROT_READBACK_SLICES", "MANDELBROT_INFLIGHT",
            "MANDELBROT_ACCELERATION", "MANDELBROT_LOG_DIRECTORY", "MANDELBROT_VIEWPORT_LOG"];
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        bool batchSweep = fixture.StartsWith("batch:", StringComparison.Ordinal);
        if (batchSweep) fixture = fixture[6..];
        var selected = fixture == "reported-deep"
            ? (Real: -0.34529529051392366, Imaginary: 0.63220042588156822,
                Scale: Math.ScaleB(1, -62), Budget: IterationBudget.ForScale(Math.ScaleB(1, -62)))
            : ProductionChecks.Fixture(fixture);
        int? previousBatchScale = GpuDispatchPolicy.BatchScaleOverride;
        using var full = MandelbrotViewport.FullSet(width, height);
        using var c = new MpfrComplex(MpfrFloat.FromDouble(selected.Real, 384), MpfrFloat.FromDouble(selected.Imaginary, 384));
        using var view = full.Zoom(c, selected.Scale);
        using var log = new StreamWriter(output) { AutoFlush = true };
        void Write(object value) => log.WriteLine(JsonSerializer.Serialize(value));
        var configurations = batchSweep
            ? new (string Name, bool Diagnostics, int Slice, int Readback, bool Publish, int BatchScale)[] {
                ("batch-1", false, 128, 4, false, 1),
                ("batch-2", false, 128, 4, false, 2),
                ("batch-4", false, 128, 4, false, 4) }
            : new (string Name, bool Diagnostics, int Slice, int Readback, bool Publish, int BatchScale)[] {
                ("journal-128", true, 128, 4, false, 1),
                ("quiet-128", false, 128, 4, false, 1),
                ("quiet-256", false, 256, 4, false, 1),
                ("quiet-512", false, 512, 4, false, 1),
                ("quiet-128-r8", false, 128, 8, false, 1),
                ("publish-128", false, 128, 4, true, 1) };
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
            Environment.SetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG", null);
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
            Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
            Environment.SetEnvironmentVariable("MANDELBROT_LOG_DIRECTORY", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "dispatch-logs"));
            Write(new { phase = "metadata", utc = DateTime.UtcNow, width, height, fixture, batchSweep, selected.Budget,
                roundedScreenshotCoordinates = fixture == "reported-deep",
                x = view.CenterX.ToExactBinary64Terms(), y = view.CenterY.ToExactBinary64Terms(), span = view.Height.ToExactBinary64Terms(),
                adapter = ComputeSharp.GraphicsDevice.GetDefault().Name,
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MandelbrotRenderer).Assembly.Location))) });
            // Validate the baseline explicitly; configuration ordering must not
            // accidentally bypass the independent MPFR sample check.
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
            Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
            GpuDispatchPolicy.BatchScaleOverride = 1;
            int[] baselineIterations = Enumerable.Repeat(EscapeClassification.Pending, width * height).ToArray();
            void Capture(int[] destination, int[] values, int offset, int count, int[]? indices)
            {
                for (int i = offset; i < offset + count; i++)
                    if (values[i] >= EscapeClassification.Interior)
                        destination[indices is null ? i : indices[i]] = values[i];
            }
            var baseline = new MandelbrotRenderer(width, height) { PublishPixels = (v, o, n, ix) => Capture(baselineIterations, v, o, n, ix) }.Render(view, selected.Budget);
            if (baseline.Validation is not { Mismatches: 0, Unresolved: 0 } || baseline.UnresolvedGlitchCount != 0)
                throw new Exception("Independent MPFR baseline validation failed.");
            Write(new { phase = "validated-baseline", baseline.Validation, baseline.RepairedCount, baseline.Float64GlitchCount,
                distinctEscapeCounts = baselineIterations.Distinct().Count(), baseline.Timings });
            for (int round = -1; round < 4; round++)
            foreach (var config in (round % 2 == 0 ? configurations : configurations.Reverse()))
            {
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", config.Diagnostics ? "1" : "0");
                Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", null);
                GpuDispatchPolicy.BatchScaleOverride = config.BatchScale;
                Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", config.Slice.ToString());
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", config.Readback.ToString());
                ProgressiveFrame? progress = config.Publish ? new(width * height, selected.Budget) : null;
                int[] raw = Enumerable.Repeat(EscapeClassification.Pending, width * height).ToArray();
                var renderer = new MandelbrotRenderer(width, height) { ProfileTimings = true,
                    PublishPixels = (v, o, n, ix) => { Capture(raw, v, o, n, ix); progress?.Publish(v, o, n, ix); } };
                Write(new { phase = "started", round, config.Name, utc = DateTime.UtcNow });
                var result = renderer.Render(view, selected.Budget);
                if (result.UnresolvedGlitchCount != 0 || result.Timings.MaxDispatchMilliseconds >= 1000)
                    throw new Exception("Throughput profile exceeded correctness/submission limits.");
                if (!raw.SequenceEqual(baselineIterations) || !result.Pixels.SequenceEqual(baseline.Pixels)
                    || result.RepairedCount != baseline.RepairedCount || result.Float64GlitchCount != baseline.Float64GlitchCount)
                    throw new Exception($"Configuration {config.Name} changed image/recovery classifications.");
                Write(new { phase = "completed", round, config.Name, config.Slice, config.Readback, config.Publish, config.BatchScale,
                    utc = DateTime.UtcNow, result.Timings, result.ReferencePasses, result.RepairedCount,
                    result.Float64GlitchCount, result.Validation,
                    hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(result.Pixels.AsSpan()))) });
                Console.WriteLine($"{config.Name} round {round}: {result.Timings.TotalMilliseconds:F1} ms, FP64 {result.Timings.Float64SliceLoopMilliseconds:F1}, DD {result.Timings.DoubleDoubleSliceLoopMilliseconds:F1}, journal {result.Timings.JournalMilliseconds:F1}, {result.Timings.DispatchCount} submissions");
            }
            Write(new { phase = "passed" });
        }
        finally {
            GpuDispatchPolicy.BatchScaleOverride = previousBatchScale;
            for (int i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]);
        }
    }
}
