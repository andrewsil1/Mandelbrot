using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

internal static class BlaProfilingChecks
{
    private static readonly Assembly Assembly = typeof(MandelbrotRenderer).Assembly;
    private static readonly Type Buffer = Assembly.GetType("MandelbrotGpu.PerturbationBuffers")!;
    private static readonly FieldInfo Profiles = typeof(MandelbrotRenderer).GetField("blaPassProfiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Raw = typeof(MandelbrotRenderer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(m => m.Name == "RenderPerturbationDoubleDouble" && m.GetParameters().Length == 5);
    private static readonly MethodInfo Mpfr = Assembly.GetType("MandelbrotGpu.MpfrMandelbrot")!.GetMethod("EscapeIterations")!;
    private static readonly string[] Variables = ["MANDELBROT_ACCELERATION", "MANDELBROT_BLA_PROFILE",
        "MANDELBROT_DIAGNOSTICS", "MANDELBROT_METRICS", "MANDELBROT_VALIDATE",
        "MANDELBROT_SLICE_ITERATIONS", "MANDELBROT_READBACK_SLICES", "MANDELBROT_INFLIGHT", "MANDELBROT_BLA_MIN_BLOCK"];

    public static void CheckConfiguration()
    {
        Type policy = Assembly.GetType("MandelbrotGpu.BlaPassProfile")!;
        foreach (string variable in new[] { "MANDELBROT_BLA_PROFILE" })
        {
            string? previous = Environment.GetEnvironmentVariable(variable);
            PropertyInfo property = policy.GetProperty("Enabled")!;
            try
            {
                Environment.SetEnvironmentVariable(variable, null);
                if ((bool)property.GetValue(null)!) throw new Exception("BLA policy default failed.");
                foreach (string setting in new[] { "1", "0" })
                {
                    Environment.SetEnvironmentVariable(variable, setting);
                    if ((bool)property.GetValue(null)! != (setting == "1")) throw new Exception("BLA policy override failed.");
                }
                Environment.SetEnvironmentVariable(variable, "invalid");
                try { property.GetValue(null); throw new Exception("Invalid BLA policy accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
            finally { Environment.SetEnvironmentVariable(variable, previous); }
        }
        MethodInfo minimumBlock = Assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!
            .GetMethod("BlaMinimumBlockLength")!;
        string? previousMinimum = Environment.GetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", null);
            if ((int)minimumBlock.Invoke(null, null)! != 4) throw new Exception("BLA minimum block default failed.");
            foreach (int value in new[] { 2, 4, 8, 16 })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", value.ToString());
                if ((int)minimumBlock.Invoke(null, null)! != value) throw new Exception("BLA minimum block override failed.");
            }
            foreach (string value in new[] { "1", "3", "32", "invalid" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", value);
                try { minimumBlock.Invoke(null, null); throw new Exception("Invalid BLA minimum block accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", previousMinimum); }
        Console.WriteLine("GPU-free BLA profiling and minimum-block policy passed.");
    }

    private static void Configure()
    {
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
        Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
        Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
    }

    private static long[] CheckProfile(object profile)
    {
        Type type = profile.GetType();
        long[] counters = (long[])type.GetProperty("Counters")!.GetValue(profile)!;
        long skipped = (long)type.GetProperty("SkippedIterations")!.GetValue(profile)!;
        if (!(bool)type.GetProperty("GpuCountersAvailable")!.GetValue(profile)!)
        {
            if (counters.Any(c => c != 0)) throw new Exception("Release collected detailed BLA GPU counters.");
            return counters;
        }
        if (counters.Any(c => c < 0) || counters[1] != counters[2] + counters[3] + counters[4] + counters[17]
            || counters[5] != counters.Skip(10).Take(7).Sum()
            || skipped != Enumerable.Range(0, 7).Sum(bin => counters[10 + bin] * (2L << bin))
            || ((int)type.GetProperty("UsableNodes")!.GetValue(profile)! == 0 && counters[0] != 0))
            throw new Exception("BLA candidate partition, histogram or no-table fast path failed.");
        return counters;
    }

    public static void CheckRendering()
    {
        string?[] previous = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        long accepted = 0, searches = 0, skipped = 0;
        try
        {
            Configure();
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
            Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");
            foreach (double scale in new[] { Math.ScaleB(1, -40), 1E-20, 1E-28 })
            {
                const int width = 513, height = 129, budget = 1024;
                using MpfrComplex center = new(MpfrFloat.FromDouble(-0.743643887037151, 384), MpfrFloat.FromDouble(0.13182590420533, 384));
                MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, scale);
                using MpfrComplex alternate = view.PointAtPixel(width / 2, height / 2, width, height);
                int[][] sets = [Enumerable.Range(0, width * height).Where(i => i % 2 == 0).Reverse().ToArray(),
                    Enumerable.Range(0, width * height).Where(i => Math.Abs(i % width - width / 2) <= 3 && Math.Abs(i / width - height / 2) <= 3).Reverse().ToArray()];
                foreach (int[] indices in sets)
                foreach (MpfrComplex reference in new[] { center, alternate })
                {
                    (int[] Values, object? Profiles) Run(string mode, bool profile)
                    {
                        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", mode);
                        Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", profile ? "1" : "0");
                        using IDisposable buffers = (IDisposable)Activator.CreateInstance(Buffer, GraphicsDevice.GetDefault(), indices.Length, budget)!;
                        MandelbrotRenderer renderer = new(width, height);
                        return ((int[])Raw.Invoke(renderer, [view, reference, budget, indices, buffers])!, Profiles.GetValue(renderer));
                    }
                    var rebase = Run("rebase", false);
                    Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", "2");
                    var accelerated = Run("bla", true);
                    var quiet = Run("bla", false);
                    if (!accelerated.Values.SequenceEqual(quiet.Values) || quiet.Profiles is not null)
                        throw new Exception("BLA profiling changed raw results or leaked collection into the disabled path.");
                    foreach (object profile in (System.Collections.IEnumerable)accelerated.Profiles!)
                    {
                        long[] counters = CheckProfile(profile);
                        accepted += counters[5]; searches += counters[0];
                        skipped += (long)profile.GetType().GetProperty("SkippedIterations")!.GetValue(profile)!;
                    }
                    foreach (int position in Enumerable.Range(0, indices.Length).Where(i => i % Math.Max(1, indices.Length / 64) == 0))
                    {
                        using MpfrComplex point = view.PointAtPixel(indices[position] % width, indices[position] / width, width, height);
                        int expected = (int)Mpfr.Invoke(null, [point, budget, (uint)768])!;
                        foreach (int value in new[] { rebase.Values[position], accelerated.Values[position] })
                            if (value != -2 && value != expected) throw new Exception("Sparse BLA raw count failed 768-bit MPFR.");
                    }
                    if (accelerated.Values.Where((v, i) => v != -2 && rebase.Values[i] != -2 && v != rebase.Values[i]).Any())
                        throw new Exception("Sparse BLA/rebase trusted classifications differ.");
                    foreach (string minimum in new[] { "4", "8", "16" })
                    {
                        Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", minimum);
                        var threshold = Run("bla", true);
                        foreach (object profile in (System.Collections.IEnumerable)threshold.Profiles!) CheckProfile(profile);
                        if (threshold.Values.Where((v, i) => v != -2 && rebase.Values[i] != -2 && v != rebase.Values[i]).Any())
                            throw new Exception($"BLA minimum block {minimum} changed a trusted classification.");
                    }
                }
            }
            if (skipped == 0) throw new Exception("BLA diagnostics fixtures did not exercise skips.");
#if DEBUG
            if (accepted == 0 || searches == 0) throw new Exception("Debug BLA diagnostics fixtures did not exercise searches/accepted blocks.");
#endif
        }
        finally { for (int i = 0; i < Variables.Length; i++) Environment.SetEnvironmentVariable(Variables[i], previous[i]); }
        Console.WriteLine($"Sparse BLA raw/profile validation passed: 768-bit MPFR, alternate references, reversed/duplicate-free sparse maps, 32768 seam, exact profiling equivalence; skipped={skipped}, detailed searches={searches}, accepted={accepted} (Debug-only counters).");
    }

    public static void Run(int width, int height, string fixture, string path)
    {
        string?[] previous = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        void Write(object value) => File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
        string[] modes = ["rebase", "bla"];
        try
        {
            Configure();
            var selected = ProductionChecks.Fixture(fixture);
            using MpfrComplex center = new(MpfrFloat.FromDouble(selected.Real, 384), MpfrFloat.FromDouble(selected.Imaginary, 384));
            MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, selected.Scale);
            int[]? baseline = null;
            foreach (string mode in modes)
            {
                Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", mode == "rebase" ? "rebase" : "bla");
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", "4");
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", "1");
                MandelbrotRenderer renderer = new(width, height);
                RenderResult result = renderer.Render(view, selected.Budget);
                if (result.Validation is not { Mismatches: 0, Unresolved: 0 } || result.UnresolvedGlitchCount != 0)
                    throw new Exception("BLA profiling pipeline failed MPFR/unresolved validation.");
                baseline ??= result.Pixels;
                if (!baseline.SequenceEqual(result.Pixels)) throw new Exception("BLA mode changed complete image.");
                if (Profiles.GetValue(renderer) is System.Collections.IEnumerable profiles)
                    foreach (object profile in profiles) CheckProfile(profile);
                Write(new { phase = "profile", width, height, fixture, mode, result.Validation,
                    result.ReferencePasses, result.RepairedCount, result.Timings.Rebases,
                    result.Timings.SkippedIterations, result.Timings.ScalarIterations,
                    ddPasses = Profiles.GetValue(renderer) });
            }
            string journal = (string)Assembly.GetType("MandelbrotGpu.DispatchJournal")!.GetProperty("LogPath")!.GetValue(null)!;
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
            Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", "0");
            // One warmup per mode followed by two balanced forward/reverse blocks.
            int[] order = [0, 1, 0, 1, 1, 0, 0, 1, 1, 0];
            for (int i = 0; i < order.Length; i++)
            {
                string mode = modes[order[i]];
                Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", mode == "rebase" ? "rebase" : "bla");
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", "4");
                MandelbrotRenderer renderer = new(width, height);
                Write(new { phase = "started", i, mode, warmup = i < modes.Length });
                RenderResult result;
                double elapsed;
                using (FileStream locked = new(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = locked.Length;
                    Stopwatch timer = Stopwatch.StartNew();
                    result = renderer.Render(view, selected.Budget);
                    elapsed = timer.Elapsed.TotalMilliseconds;
                    if (locked.Length != length) throw new Exception("Quiet BLA benchmark wrote renderer logs.");
                }
                if (!baseline!.SequenceEqual(result.Pixels) || result.UnresolvedGlitchCount != 0 || result.Validation is not null
                    || Profiles.GetValue(renderer) is not null
                    || typeof(RenderTimings).GetProperties().Any(p => Convert.ToDouble(p.GetValue(result.Timings)) != 0))
                    throw new Exception("Quiet BLA benchmark changed image or collected diagnostics.");
                Write(new { phase = "completed", i, mode, warmup = i < modes.Length, milliseconds = elapsed,
                    result.ReferencePasses, result.RepairedCount, exactImage = true });
                Console.WriteLine($"BLA PROFILE {i + 1}/{order.Length}: {mode}, {elapsed:n1} ms; exact image.");
            }
            Write(new { phase = "passed" });
        }
        finally { for (int i = 0; i < Variables.Length; i++) Environment.SetEnvironmentVariable(Variables[i], previous[i]); }
    }

    public static void RunSparse(int width, int height, string path)
    {
        string?[] previous = Variables.Select(Environment.GetEnvironmentVariable).ToArray();
        void Write(object value) => File.AppendAllText(path, JsonSerializer.Serialize(value) + Environment.NewLine);
        const int budget = 4096;
        string[] modes = ["rebase", "bla2", "bla4", "bla8", "bla16"];
        try
        {
            Configure();
            using MpfrComplex center = new(MpfrFloat.FromDouble(-0.743643887037151, 384), MpfrFloat.FromDouble(0.13182590420533, 384));
            foreach (double scale in new[] { 1E-18, 1E-20, 1E-28 })
            {
                MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, scale);
                int[] indices = Enumerable.Range(0, width * height).Where(i => Math.Abs(i % width - width / 2) < 32
                    && Math.Abs(i / width - height / 2) < 32).Reverse().ToArray();
                int[][] baselines = new int[modes.Length][];
                void Mode(int mode)
                {
                    Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", mode == 0 ? "rebase" : "bla");
                    Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", mode == 0 ? "2" : (1 << mode).ToString());
                }
                for (int mode = 0; mode < modes.Length; mode++)
                {
                    Mode(mode);
                    Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
                    Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "1");
                    Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", "1");
                    using IDisposable buffers = (IDisposable)Activator.CreateInstance(Buffer, GraphicsDevice.GetDefault(), indices.Length, budget)!;
                    MandelbrotRenderer renderer = new(width, height);
                    baselines[mode] = (int[])Raw.Invoke(renderer, [view, center, budget, indices, buffers])!;
                    foreach (object profile in (System.Collections.IEnumerable)Profiles.GetValue(renderer)!) CheckProfile(profile);
                    foreach (int position in Enumerable.Range(0, indices.Length).Where(i => i % Math.Max(1, indices.Length / 64) == 0))
                    {
                        using MpfrComplex point = view.PointAtPixel(indices[position] % width, indices[position] / width, width, height);
                        int expected = (int)Mpfr.Invoke(null, [point, budget, (uint)768])!;
                        if (baselines[mode][position] != -2 && baselines[mode][position] != expected)
                            throw new Exception("Sparse BLA benchmark failed raw 768-bit MPFR.");
                    }
                    Write(new { phase = "sparse-profile", width, height, scale, pixels = indices.Length, mode = modes[mode],
                        glitches = baselines[mode].Count(v => v == -2), ddPasses = Profiles.GetValue(renderer) });
                }
                for (int mode = 1; mode < modes.Length; mode++)
                    if (baselines[mode].Where((v, i) => v != -2 && baselines[0][i] != -2 && v != baselines[0][i]).Any())
                        throw new Exception("Sparse BLA modes changed trusted raw counts.");
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
                Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "0");
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_PROFILE", "0");
                using IDisposable quietBuffers = (IDisposable)Activator.CreateInstance(Buffer, GraphicsDevice.GetDefault(), indices.Length, budget)!;
                string journal = (string)Assembly.GetType("MandelbrotGpu.DispatchJournal")!.GetProperty("LogPath")!.GetValue(null)!;
                int[] order = [0, 1, 2, 3, 4,
                    0, 1, 2, 3, 4, 4, 3, 2, 1, 0,
                    0, 1, 2, 3, 4, 4, 3, 2, 1, 0];
                for (int i = 0; i < order.Length; i++)
                {
                    int mode = order[i];
                    Mode(mode);
                    MandelbrotRenderer renderer = new(width, height);
                    Write(new { phase = "sparse-started", scale, i, mode = modes[mode], warmup = i < modes.Length });
                    double elapsed;
                    int[] values;
                    using (FileStream locked = new(journal, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        long length = locked.Length;
                        Stopwatch timer = Stopwatch.StartNew();
                        values = (int[])Raw.Invoke(renderer, [view, center, budget, indices, quietBuffers])!;
                        elapsed = timer.Elapsed.TotalMilliseconds;
                        if (locked.Length != length) throw new Exception("Sparse quiet pass changed its log.");
                    }
                    RenderTimings timings = (RenderTimings)typeof(MandelbrotRenderer).GetField("timings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
                    if (!values.SequenceEqual(baselines[mode]) || Profiles.GetValue(renderer) is not null
                        || typeof(RenderTimings).GetProperties().Any(p => Convert.ToDouble(p.GetValue(timings)) != 0))
                        throw new Exception("Sparse quiet pass changed counts or collected diagnostics.");
                    Write(new { phase = "sparse-completed", scale, i, mode = modes[mode], warmup = i < modes.Length, milliseconds = elapsed, exactRaw = true });
                    Console.WriteLine($"SPARSE BLA {scale:E}/{i + 1}: {modes[mode]}, {elapsed:n1} ms, exact raw classifications.");
                }
            }
            Write(new { phase = "passed" });
        }
        finally { for (int i = 0; i < Variables.Length; i++) Environment.SetEnvironmentVariable(Variables[i], previous[i]); }
    }
}
