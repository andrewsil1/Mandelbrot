using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using MandelbrotGpu;

internal static class ResponsivenessChecks
{
    private static MpfrFloat Exact(JsonElement terms)
    {
        MpfrFloat result = MpfrFloat.FromDouble(0, 384);
        foreach (var term in terms.EnumerateArray())
        {
            var next = result.Add(BitConverter.UInt64BitsToDouble(Convert.ToUInt64(term.GetString(), 16)));
            result.Dispose(); result = next;
        }
        return result;
    }

    public static void Profile(int width, int height, string input, string output, bool attribution = false)
    {
        if (File.Exists(output)) throw new IOException("Refusing to overwrite a profile.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using StreamWriter log = new(output) { AutoFlush = true };
        void Write(object value) => log.WriteLine(JsonSerializer.Serialize(value));
        Environment.SetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG", null);
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
        // The final two starts in the completed live session are the zoomed tip
        // and exact transition fixture. Preserve every stored coordinate bit.
        var fixtures = File.ReadLines(input).Select(line => JsonDocument.Parse(line))
            .Where(doc => doc.RootElement.GetProperty("phase").GetString() == "started").TakeLast(2).ToArray();
        Write(new { phase = "metadata", width, height, input, attribution, runtime = Environment.Version.ToString(),
            adapter = ComputeSharp.GraphicsDevice.GetDefault().Name,
            slices = GpuDispatchPolicy.SliceIterations(6912), readback = GpuDispatchPolicy.ReadbackSlices(), inFlight = GpuDispatchPolicy.InFlightSubmissions(),
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MandelbrotRenderer).Assembly.Location))) });
        foreach (var doc in fixtures)
        {
            var f = doc.RootElement;
            using var x = Exact(f.GetProperty("x"));
            using var y = Exact(f.GetProperty("y"));
            using var span = Exact(f.GetProperty("span"));
            using var center = new MpfrComplex(x.Clone(), y.Clone());
            // Reconstruct directly: no decimal or binary64 round-trip for span.
            var ctor = typeof(MandelbrotViewport).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
            var view = (MandelbrotViewport)ctor.Invoke([x.Clone(), y.Clone(), span.Clone(), (double)width / height]);
            string fixture = x.ToDouble() == -2 ? "tip" : "transition";
            int budget = f.GetProperty("budget").GetInt32();
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
            RenderResult baseline = new MandelbrotRenderer(width, height).Render(view, budget);
            if (baseline.Validation is not { Mismatches: 0, Unresolved: 0 } || baseline.UnresolvedGlitchCount != 0)
                throw new Exception("Profile baseline failed MPFR validation.");
            string hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(baseline.Pixels.AsSpan())));
            Write(new { phase = "baseline", fixture, width, height, budget, hash, baseline.ReferencePasses,
                baseline.RepairedCount, baseline.Float64GlitchCount, baseline.Timings, baseline.Validation });
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
            using var process = Process.GetCurrentProcess();
            using FileStream journalLock = new(DispatchJournal.LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long journalLength = journalLock.Length;
            for (int run = 0; run < (attribution ? 8 : 3); run++)
            {
                bool measured = attribution && (run % 4 == 1 || run % 4 == 2);
                Write(new { phase = "started", fixture, run, utc = DateTime.UtcNow });
                var cpu = process.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                RenderResult result = new MandelbrotRenderer(width, height) { ProfileTimings = measured }.Render(view, budget);
                timer.Stop(); process.Refresh();
                if (journalLock.Length != journalLength) throw new Exception("Quiet attribution wrote to the journal.");
                if (!baseline.Pixels.SequenceEqual(result.Pixels) || result.UnresolvedGlitchCount != 0 || result.Validation is not null)
                    throw new Exception("Quiet frame differs from validated baseline.");
                if (!measured && typeof(RenderTimings).GetProperties().Any(p => Convert.ToDouble(p.GetValue(result.Timings)) != 0))
                    throw new Exception("Quiet frame collected diagnostics.");
                if (measured && (result.Timings.JournalWriteCount != 0 || result.Timings.TotalMilliseconds <= 0
                    || result.Timings.OtherHostMilliseconds < -0.1
                    || Math.Abs(result.Timings.DispatchMilliseconds - result.Timings.SubmissionMilliseconds - result.Timings.CompletionWaitMilliseconds) > 0.1))
                    throw new Exception("Attribution accounting failed.");
                Write(new { phase = "completed", fixture, width, height, run, measured, warmup = attribution && run < 2,
                    timings = measured ? result.Timings : null, milliseconds = timer.Elapsed.TotalMilliseconds,
                    cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, privateBytes = process.PrivateMemorySize64,
                    result.ReferencePasses, result.RepairedCount, result.Float64GlitchCount, hash });
                Console.WriteLine($"QUIET {fixture} {width}x{height} run {run}: {timer.Elapsed.TotalMilliseconds:F1} ms, exact validated image");
            }
            view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose(); doc.Dispose();
        }
        Write(new { phase = "passed" });
    }
}
