using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using MandelbrotGpu;

internal static class Fp32ExperimentChecks
{
    private static readonly MethodInfo Fp64 = typeof(MandelbrotRenderer).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(m => m.Name == "RenderPerturbationFloat64" && m.GetParameters().Length == 3);

    private static readonly (string Name, double X, double Y, double Scale, int Budget)[] Fixtures =
    [
        ("escaping", 1, 0, 0.25, 256),
        ("deep-escaping", 1.1, 0.2, 1E-28, 256),
        ("tip", -2, 0, 1E-28, 512),
        ("i-boundary", 0, 1, 1E-28, 512),
        ("range-bypass", 0, 1, 1E-60, 512),
        ("filament", -0.743643887037151, 0.13182590420533, 1E-20, 4096),
        ("filament-deeper", -0.743643887037151, 0.13182590420533, 1E-28, 4096),
        ("component", -1.7548776662466927, 0, 1E-28, 1024),
        ("transition", -0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -40), 6912)
    ];

    public static void Run()
    {
        string? diag = Environment.GetEnvironmentVariable("MANDELBROT_DIAGNOSTICS");
        string? slices = Environment.GetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS");
        string? readback = Environment.GetEnvironmentVariable("MANDELBROT_READBACK_SLICES");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
            const int w = 17, h = 9;
            int accepted = 0;
            foreach (var f in Fixtures)
            {
                using MpfrComplex center = new(MpfrFloat.FromDouble(f.X, 384), MpfrFloat.FromDouble(f.Y, 384));
                var view = MandelbrotViewport.FullSet(w, h).Zoom(center, f.Scale);
                try
                {
                    int[] expected = Enumerable.Range(0, w * h).Select(index =>
                    {
                        using var point = view.PointAtPixel(index % w, index / w, w, h);
                        return MpfrMandelbrot.EscapeIterations(point, f.Budget, 768);
                    }).ToArray();
                    int[]? first = null;
                    foreach (var policy in new[] { (Slice: "128", Read: "4"), (Slice: "32", Read: "1") })
                    {
                        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", policy.Slice);
                        Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", policy.Read);
                        int[] raw = new MandelbrotRenderer(w, h).RenderExperimentalFp32(view, f.Budget);
                        if (raw.Where((v, i) => v != EscapeClassification.Glitch && v != expected[i]).Any())
                            throw new Exception($"FP32 {f.Name}: trusted raw count differs from 768-bit MPFR.");
                        if (first is not null && !first.SequenceEqual(raw)) throw new Exception("FP32 slice/readback changed classifications.");
                        first = raw;
                    }
                    int trusted = first!.Count(v => v != EscapeClassification.Glitch);
                    if (f.Name == "deep-escaping" && trusted != w * h) throw new Exception("Deep FP32 escape acceptance regressed.");
                    if (f.Name == "range-bypass" && trusted != 0) throw new Exception("Unsupported FP32 range was accepted.");
                    accepted += trusted;
                    Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
                    Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
                    if (f.Scale < 1E-12)
                    {
                        var baseline = new MandelbrotRenderer(w, h).Render(view, f.Budget);
                        var candidate = new MandelbrotRenderer(w, h) { ExperimentalFp32 = true }.Render(view, f.Budget);
                        if (candidate.UnresolvedGlitchCount != 0 || !candidate.Pixels.SequenceEqual(baseline.Pixels)
                            || !candidate.Pixels.SequenceEqual(HistogramColorizer.Colorize(expected, f.Budget)))
                            throw new Exception($"FP32 {f.Name}: full pipeline differs from MPFR or existing renderer.");
                    }
                    Console.WriteLine($"FP32 {f.Name}: {trusted}/{w*h} trusted, all-pixel MPFR and slice equivalence passed.");
                }
                finally { Dispose(view); }
            }
            if (accepted == 0) throw new Exception("FP32 checks accepted no pixels.");
            // Multiple batches with a partial final group; compare against MPFR
            // on every pixel of a low-budget bounded view.
            const int bw = 257, bh = 131, bb = 32;
            using MpfrComplex bc = new(MpfrFloat.FromDouble(1, 384), MpfrFloat.FromDouble(0, 384));
            var bv = MandelbrotViewport.FullSet(bw, bh).Zoom(bc, 0.25);
            try
            {
                int[] raw = new MandelbrotRenderer(bw, bh).RenderExperimentalFp32(bv, bb);
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == EscapeClassification.Glitch) continue;
                    using var point = bv.PointAtPixel(i % bw, i / bw, bw, bh);
                    if (raw[i] != MpfrMandelbrot.EscapeIterations(point, bb, 768)) throw new Exception("FP32 batch seam mismatch.");
                }
                int[] original = (int[])Fp64.Invoke(new MandelbrotRenderer(bw, bh), [bv, bc, bb])!;
                int[] seed = original.Select((v, i) => i % 3 == 0 ? v : EscapeClassification.Glitch).ToArray();
                MethodInfo seeded = typeof(MandelbrotRenderer).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Single(m => m.Name == "RenderPerturbationFloat64" && m.GetParameters().Length == 4);
                int[] merged = (int[])seeded.Invoke(new MandelbrotRenderer(bw, bh), [bv, bc, bb, seed])!;
                if (!merged.SequenceEqual(original)) throw new Exception("Seeded FP64 changed counts across batch seams.");
                Console.WriteLine("FP32 multi-batch/tail raw MPFR check passed.");
            }
            finally { Dispose(bv); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", diag);
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", slices);
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", readback);
        }
    }

    private static MpfrFloat Exact(JsonElement terms)
    {
        var value = MpfrFloat.FromDouble(0, 384);
        foreach (var term in terms.EnumerateArray())
        {
            var next = value.Add(BitConverter.UInt64BitsToDouble(Convert.ToUInt64(term.GetString(), 16)));
            value.Dispose(); value = next;
        }
        return value;
    }

    private static void Dispose(MandelbrotViewport view)
    { view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose(); }

    public static void Profile(int width, int height, string input, string output)
    {
        if (width > 1024) throw new ArgumentOutOfRangeException(nameof(width));
        if (File.Exists(output)) throw new IOException("Refusing to overwrite a profile.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using StreamWriter log = new(output) { AutoFlush = true };
        void Write(object value) => log.WriteLine(JsonSerializer.Serialize(value));
        Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG", null);
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", null);
        Write(new { phase = "metadata", width, height, input, adapter = ComputeSharp.GraphicsDevice.GetDefault().Name,
            candidate = "scalar rescaled FP32 then seeded full-grid FP64, existing DD/MPFR recovery",
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MandelbrotRenderer).Assembly.Location))) });
        var entries = File.ReadLines(input).Select(line => JsonDocument.Parse(line))
            .Where(doc => doc.RootElement.GetProperty("phase").GetString() == "started").TakeLast(2).ToArray();
        foreach (var doc in entries)
        {
            using (doc)
            {
                var f = doc.RootElement;
                using var x = Exact(f.GetProperty("x")); using var y = Exact(f.GetProperty("y")); using var span = Exact(f.GetProperty("span"));
                var ctor = typeof(MandelbrotViewport).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                var view = (MandelbrotViewport)ctor.Invoke([x.Clone(), y.Clone(), span.Clone(), (double)width / height]);
                try { Measure(x.ToDouble() == -2 ? "tip" : "transition", view, f.GetProperty("budget").GetInt32()); }
                finally { Dispose(view); }
            }
        }
        foreach (var f in Fixtures.Where(f => f.Name is "filament" or "filament-deeper" or "component" or "i-boundary" or "range-bypass"))
        {
            using MpfrComplex center = new(MpfrFloat.FromDouble(f.X, 384), MpfrFloat.FromDouble(f.Y, 384));
            var view = MandelbrotViewport.FullSet(width, height).Zoom(center, f.Scale);
            try { Measure(f.Name, view, f.Budget); }
            finally { Dispose(view); }
        }
        Write(new { phase = "passed" });

        void Measure(string name, MandelbrotViewport view, int budget)
        {
            using var center = new MpfrComplex(view.CenterX.Clone(), view.CenterY.Clone());
            var rawTimer = Stopwatch.StartNew();
            int[] raw = new MandelbrotRenderer(width, height).RenderExperimentalFp32(view, budget);
            double rawMs = rawTimer.Elapsed.TotalMilliseconds;
            int[] fp = (int[])Fp64.Invoke(new MandelbrotRenderer(width, height), [view, center, budget])!;
            if (raw.Where((v, i) => v != EscapeClassification.Glitch && fp[i] != EscapeClassification.Glitch && v != fp[i]).Any())
                throw new Exception($"{name}: FP32/FP64 trusted raw disagreement.");
            // Validate regular samples plus every FP32-only acceptance: recovery
            // and coloring must not conceal a wrongly trusted cheap-pass count.
            var samples = Enumerable.Range(0, raw.Length).Where(i => i % Math.Max(1, raw.Length / 256) == 0
                || (raw[i] != EscapeClassification.Glitch && fp[i] == EscapeClassification.Glitch)).ToArray();
            foreach (int i in samples)
            {
                using var point = view.PointAtPixel(i % width, i / width, width, height);
                int expected = MpfrMandelbrot.EscapeIterations(point, budget, 768);
                if ((raw[i] != EscapeClassification.Glitch && raw[i] != expected)
                    || (fp[i] != EscapeClassification.Glitch && fp[i] != expected)) throw new Exception($"{name}: raw MPFR mismatch at {i}.");
            }
            var baseline = new MandelbrotRenderer(width, height).Render(view, budget);
            if (baseline.UnresolvedGlitchCount != 0) throw new Exception("Incomplete baseline excluded from timing.");
            Write(new { phase = "raw", name, width, height, budget, rawMs, accepted = raw.Count(v => v != EscapeClassification.Glitch),
                fp64Accepted = fp.Count(v => v != EscapeClassification.Glitch), samples = samples.Length,
                hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(baseline.Pixels.AsSpan()))) });
            int[] order = [0, 1, 0, 1, 1, 0, 0, 1, 1, 0];
            for (int run = 0; run < order.Length; run++)
            {
                bool candidate = order[run] == 1;
                var renderer = new MandelbrotRenderer(width, height) { ExperimentalFp32 = candidate };
                var timer = Stopwatch.StartNew();
                var result = renderer.Render(view, budget);
                timer.Stop();
                if (result.UnresolvedGlitchCount != 0 || !result.Pixels.SequenceEqual(baseline.Pixels))
                    throw new Exception($"{name}: candidate pipeline image mismatch.");
                Write(new { phase = "completed", name, width, height, candidate, run, warmup = run < 2,
                    milliseconds = timer.Elapsed.TotalMilliseconds, renderer.ExperimentalAccepted,
                    result.Float64GlitchCount, result.ReferencePasses, result.RepairedCount });
                Console.WriteLine($"FP32 PROFILE {name} {(candidate ? "candidate" : "baseline")}: {timer.Elapsed.TotalMilliseconds:F2} ms, exact image.");
            }
        }
    }
}
