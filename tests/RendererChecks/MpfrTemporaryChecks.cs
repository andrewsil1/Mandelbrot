using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MandelbrotGpu;

internal static class MpfrTemporaryChecks
{
    public static void Run()
    {
        int checkedCount = 0;
        foreach (var f in new (double X, double Y, double Scale, int Budget)[] {
            (0, 1, 1E-28, 512), (0, 1, 1E-60, 1024), (-2, 0, 1E-28, 512),
            (-0.743643887037151, 0.13182590420533, 1E-13, 4096),
            (-0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -40), 6912),
            (0.25, 0, 1E-28, 512), (-1.25, 0, 1E-28, 512), (4, 0, 1E-8, 64) })
        {
            using MpfrComplex center = new(MpfrFloat.FromDouble(f.X, 384), MpfrFloat.FromDouble(f.Y, 384));
            var full = MandelbrotViewport.FullSet(7, 5);
            var view = full.Zoom(center, f.Scale);
            try {
                for (int i = 0; i < 35; i++) {
                    using var c = view.PointAtPixel(i % 7, i / 7, 7, 5);
                    foreach (uint precision in new uint[] { 384, 768 }) {
                        int actual = MpfrMandelbrot.EscapeIterations(c, f.Budget, precision);
                        int expected = AllocatingMpfrBaseline.EscapeIterations(c, f.Budget, precision);
                        if (actual != expected) throw new Exception($"MPFR reuse mismatch at {f}, pixel {i}, precision {precision}.");
                    }
                    if (MpfrMandelbrot.EscapeIterations(c, f.Budget, 384) != AllocatingMpfrBaseline.EscapeIterations(c, f.Budget, 768))
                        throw new Exception($"MPFR 768-bit classification mismatch at {f}, pixel {i}.");
                    checkedCount++;
                }
            }
            finally { Dispose(view); Dispose(full); }
        }
        using var origin = new MpfrComplex(MpfrFloat.FromDouble(0, 384), MpfrFloat.FromDouble(0, 384));
        foreach (int budget in new[] { -1, 0, 1, 2, 64 })
            if (MpfrMandelbrot.EscapeIterations(origin, budget) != AllocatingMpfrBaseline.EscapeIterations(origin, budget))
                throw new Exception("MPFR zero/interior iteration semantics changed.");
        Console.WriteLine($"MPFR temporary checks passed: {checkedCount} boundary pixels at 384/768 bits, independent allocating baseline and zero/interior budgets.");
    }

    public static void Profile(string output)
    {
        if (File.Exists(output)) throw new IOException("Refusing to overwrite profile.");
        Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
        using var log = new StreamWriter(output) { AutoFlush = true };
        void Write(object value) => log.WriteLine(JsonSerializer.Serialize(value));
        Write(new { phase = "metadata", utc = DateTime.UtcNow, processors = Environment.ProcessorCount,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MandelbrotRenderer).Assembly.Location))),
            sourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes("MpfrMandelbrot.cs"))) });
        Run();
        const int width = 512, height = 260, budget = 6912;
        using var center = new MpfrComplex(MpfrFloat.FromDouble(-0.46729724795644717, 384), MpfrFloat.FromDouble(0.5881255899137422, 384));
        var full = MandelbrotViewport.FullSet(width, height);
        var view = full.Zoom(center, Math.ScaleB(1, -40));
        var repaired = new ConcurrentDictionary<int, int>();
        var renderer = new MandelbrotRenderer(width, height) {
            ProfileTimings = true,
            PublishPixels = (values, offset, count, indices) => {
                // At this fixed resolution full FP64 batches have more than one pixel;
                // sparse DD callbacks carry indices. Single unmapped updates are repair.
                if (count == 1 && indices is null) repaired[offset] = values[offset];
            }
        };
        var baseline = renderer.Render(view, budget);
        if (repaired.Count != baseline.RepairedCount || repaired.Count == 0 || baseline.UnresolvedGlitchCount != 0)
            throw new Exception("Repair capture incomplete.");
        var indices = repaired.Keys.Order().ToArray();
        Write(new { phase = "fixture", width, height, budget, x = view.CenterX.ToExactBinary64Terms(),
            y = view.CenterY.ToExactBinary64Terms(), span = view.Height.ToExactBinary64Terms(), indices,
            expected = indices.Select(i => repaired[i]).ToArray(), hash = Hash(baseline.Pixels), baseline.RepairedCount });
        var points = indices.Select(i => view.PointAtPixel(i % width, i / width, width, height)).ToArray();
        try {
            foreach (var point in points)
                if (MpfrMandelbrot.EscapeIterations(point, budget) != AllocatingMpfrBaseline.EscapeIterations(point, budget, 768))
                    throw new Exception("Captured repair differs from 768-bit MPFR.");
            // Warm both implementations, then alternate AB/BA ordering.
            for (int round = -1; round < 6; round++) {
                foreach (bool allocating in (round % 2 == 0 ? new[] { true, false } : new[] { false, true })) {
                    var results = new int[points.Length];
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    var watch = Stopwatch.StartNew();
                    for (int repeat = 0; repeat < 8; repeat++)
                        for (int i = 0; i < points.Length; i++)
                            results[i] = allocating ? AllocatingMpfrBaseline.EscapeIterations(points[i], budget)
                                : MpfrMandelbrot.EscapeIterations(points[i], budget);
                    watch.Stop();
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (!results.SequenceEqual(indices.Select(i => repaired[i]))) throw new Exception("Repair benchmark mismatch.");
                    Write(new { phase = "repair", round, allocating, repeats = 8, pixels = points.Length,
                        milliseconds = watch.Elapsed.TotalMilliseconds, managedBytes = allocated });
                }
            }
            for (int run = 0; run < 6; run++) {
                var result = new MandelbrotRenderer(width, height) { ProfileTimings = true }.Render(view, budget);
                if (!baseline.Pixels.SequenceEqual(result.Pixels) || result.RepairedCount != baseline.RepairedCount || result.UnresolvedGlitchCount != 0)
                    throw new Exception("Profile frame differs.");
                Write(new { phase = "frame", run, hash = Hash(result.Pixels), result.RepairedCount,
                    result.ReferencePasses, result.Float64GlitchCount, result.Timings });
            }
        }
        finally { foreach (var point in points) point.Dispose(); Dispose(view); Dispose(full); }
        Write(new { phase = "passed" });
        Console.WriteLine($"MPFR profile passed: {points.Length} actual repair pixels; {output}");
    }

    private static string Hash(int[] pixels) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
    private static void Dispose(MandelbrotViewport view) { view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose(); }
}
