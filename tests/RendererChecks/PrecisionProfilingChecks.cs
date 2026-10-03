using System.Diagnostics;
using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

// Measurements are an explicit harness operation, not a runtime tuning system.
internal static class PrecisionProfilingChecks
{
    private static readonly string[] Variables = ["MANDELBROT_DIAGNOSTICS", "MANDELBROT_VALIDATE", "MANDELBROT_METRICS",
        "MANDELBROT_ACCELERATION", "MANDELBROT_BLA_PROFILE", "MANDELBROT_BLA_MIN_BLOCK", "MANDELBROT_INFLIGHT",
        "MANDELBROT_READBACK_SLICES", "MANDELBROT_SLICE_ITERATIONS"];

    private static readonly (string Name, double Real, double Imaginary, double Scale, int Budget)[] Fixtures =
    [
        ("transition", -0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -40), 6912),
        ("transition-next", -0.46729724795644717, 0.5881255899137422, Math.ScaleB(1, -42), 7232),
        ("filament-20", -0.743643887037151, 0.13182590420533, 1E-20, 4096),
        ("filament-28", -0.743643887037151, 0.13182590420533, 1E-28, 4096),
        ("tip-28", -2, 0, 1E-28, 1024),
        ("preperiodic-28", 0, 1, 1E-28, 4096),
        ("preperiodic-60", 0, 1, 1E-60, 4096),
        ("period-three-28", -1.7548776662466927, 0, 1E-28, 1024),
        ("filament-60", -0.743643887037151, 0.13182590420533, 1E-60, 4096)
    ];

    internal static RenderResult Render(MandelbrotRenderer renderer, MandelbrotViewport view, int budget, bool dd)
        => renderer.RenderWithMode(view, budget, dd ? RenderMode.PerturbationDoubleDouble : RenderMode.PerturbationFloat64);

    public static void Run(int width, int height, string path)
    {
        EnvironmentScope environment = new(Variables);
        void Write(object value) => File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
        try
        {
            Write(new { phase = "metadata", adapter = GraphicsDevice.GetDefault().Name, width, height });
            Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
            Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", "0");
            Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", "4");
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
            foreach (var fixture in Fixtures)
            {
                using MpfrComplex center = new(MpfrFloat.FromDouble(fixture.Real, 384), MpfrFloat.FromDouble(fixture.Imaginary, 384));
                MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, fixture.Scale);
                int[] indices = Enumerable.Range(0, width * height).ToArray();
                int[] samples = indices.Where(i => i % Math.Max(1, indices.Length / 64) == 0).ToArray();
                int[] expected = samples.Select(i =>
                {
                    using MpfrComplex point = view.PointAtPixel(i % width, i / width, width, height);
                    return MpfrMandelbrot.EscapeIterations(point, fixture.Budget, 768);
                }).ToArray();
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
                int[][] raw = new int[2][];
                RenderResult[] baselines = new RenderResult[2];
                for (int mode = 0; mode < 2; mode++)
                {
                    bool dd = mode == 1;
                    MandelbrotRenderer renderer = new(width, height);
                    using PerturbationBuffers buffers = new(GraphicsDevice.GetDefault(), indices.Length, fixture.Budget);
                    raw[mode] = dd ? renderer.RenderPerturbationDoubleDouble(view, center, fixture.Budget, indices, buffers)
                        : renderer.RenderPerturbationFloat64(view, center, fixture.Budget);
                    for (int j = 0; j < samples.Length; j++)
                        if (raw[mode][samples[j]] != -2 && raw[mode][samples[j]] != expected[j])
                            throw new Exception($"{fixture.Name}/{dd}: trusted raw count disagrees with MPFR at {samples[j]}.");
                    Write(new { phase = "raw", fixture.Name, fixture.Scale, fixture.Budget, width, height, dd,
                        glitches = raw[mode].Count(v => v == -2), samples = samples.Length, timings = renderer.Timings });
                    baselines[mode] = Render(new(width, height), view, fixture.Budget, dd);
                    Write(new { phase = "pipeline", fixture.Name, dd, baselines[mode].Mode, baselines[mode].ReferencePasses,
                        baselines[mode].RepairedCount, baselines[mode].UnresolvedGlitchCount, baselines[mode].Validation, baselines[mode].Timings });
                    if (baselines[mode].Validation is not { Mismatches: 0 })
                        throw new Exception($"{fixture.Name}/{dd}: complete pipeline failed validation.");
                }
                if (raw[0].Where((v, i) => v != -2 && raw[1][i] != -2 && v != raw[1][i]).Any())
                    throw new Exception($"{fixture.Name}: FP64/DD trusted counts or repaired images differ.");

                // Test the existing densest-tile reference selector before adding
                // any new sparse FP64 machinery. This measures recovery, not a
                // promise about the speed of a hypothetical sparse implementation.
                int[] failed = indices.Where(i => raw[0][i] == -2).ToArray();
                if (failed.Length > 0)
                {
                    var tile = new MandelbrotRenderer(width, height).FindWorstGlitchTile(raw[0], new HashSet<int>())!.Value;
                    int x = tile.ReferenceX;
                    int y = tile.ReferenceY;
                    using MpfrComplex alternate = view.PointAtPixel(x, y, width, height);
                    MandelbrotRenderer renderer = new(width, height);
                    int[] retry = renderer.RenderPerturbationFloat64(view, alternate, fixture.Budget);
                    for (int j = 0; j < samples.Length; j++)
                        if (retry[samples[j]] != -2 && retry[samples[j]] != expected[j])
                            throw new Exception("Alternate FP64 reference failed MPFR.");
                    if (retry.Where((v, i) => v != -2 && raw[1][i] != -2 && v != raw[1][i]).Any())
                        throw new Exception("Alternate FP64 reference changed a trusted count.");
                    Write(new { phase = "alternate-reference", fixture.Name, failed = failed.Length,
                        recovered = failed.Count(i => retry[i] != -2), x, y, timings = renderer.Timings });
                }

                if (baselines.Any(result => result.UnresolvedGlitchCount != 0))
                {
                    Write(new { phase = "incomplete", fixture.Name,
                        fp64Unresolved = baselines[0].UnresolvedGlitchCount, ddUnresolved = baselines[1].UnresolvedGlitchCount });
                    Console.WriteLine($"PRECISION {fixture.Name}: incomplete recovery; no speedup comparison.");
                    continue;
                }
                if (!baselines[0].Pixels.SequenceEqual(baselines[1].Pixels))
                    throw new Exception($"{fixture.Name}: complete repaired images differ.");

                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
                Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "0");
                string journal = DispatchJournal.LogPath;
                int[] order = [0, 1, 0, 1, 1, 0, 0, 1, 1, 0];
                for (int run = 0; run < order.Length; run++)
                {
                    int mode = order[run];
                    Write(new { phase = "started", fixture.Name, run, dd = mode == 1 });
                    using FileStream locked = new(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
                    long length = locked.Length;
                    MandelbrotRenderer renderer = new(width, height);
                    Stopwatch timer = Stopwatch.StartNew();
                    RenderResult result = Render(renderer, view, fixture.Budget, mode == 1);
                    double elapsed = timer.Elapsed.TotalMilliseconds;
                    if (!result.Pixels.SequenceEqual(baselines[mode].Pixels) || result.UnresolvedGlitchCount != 0
                        || result.Validation is not null || locked.Length != length
                        || typeof(RenderTimings).GetProperties().Any(p => Convert.ToDouble(p.GetValue(result.Timings)) != 0))
                        throw new Exception("Quiet precision comparison changed images or collected diagnostics.");
                    Write(new { phase = "completed", fixture.Name, dd = mode == 1, warmup = run < 2, milliseconds = elapsed,
                        result.ReferencePasses, result.RepairedCount });
                    Console.WriteLine($"PRECISION {fixture.Name} {(mode == 1 ? "DD" : "FP64")}: {elapsed:n2} ms, exact image.");
                }
            }
            Write(new { phase = "passed" });
        }
        finally { environment.Dispose(); }
    }
}
