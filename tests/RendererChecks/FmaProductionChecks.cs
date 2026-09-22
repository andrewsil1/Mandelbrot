using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MandelbrotGpu;

internal static class FmaProductionChecks
{
    public static void Run(string frames, string prior, string output)
    {
        if (File.Exists(output)) throw new IOException("Refusing to overwrite smoke evidence.");
        var reports = File.ReadLines(prior).Select(line => JsonDocument.Parse(line)).ToArray();
        var starts = File.ReadLines(frames).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            if (reports[^1].RootElement.GetProperty("phase").GetString() != "passed") throw new Exception("Prior experiment incomplete.");
            var old = reports.Select(d => d.RootElement).First(e => e.GetProperty("phase").GetString() == "completed"
                && e.GetProperty("name").GetString() == "transition" && e.GetProperty("candidate").GetBoolean());
            int w = old.GetProperty("width").GetInt32(), h = old.GetProperty("height").GetInt32();
            var fixture = starts.Select(d => d.RootElement).Last(e => e.GetProperty("phase").GetString() == "started");
            using var x = Exact(fixture.GetProperty("x")); using var y = Exact(fixture.GetProperty("y")); using var span = Exact(fixture.GetProperty("span"));
            var ctor = typeof(MandelbrotViewport).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            var view = (MandelbrotViewport)ctor.Invoke([x.Clone(), y.Clone(), span.Clone(), (double)w / h]);
            try
            {
                Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
                Environment.SetEnvironmentVariable("MANDELBROT_METRICS", "0");
                Environment.SetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG", null);
                Environment.SetEnvironmentVariable("MANDELBROT_LOG_DIRECTORY", Path.GetFullPath(output + ".dispatch"));
                Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
                Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
                Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
                Environment.SetEnvironmentVariable("MANDELBROT_BLA_MIN_BLOCK", "4");
                var timer = Stopwatch.StartNew();
                var result = new MandelbrotRenderer(w, h).Render(view, fixture.GetProperty("budget").GetInt32());
                timer.Stop();
                string hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(result.Pixels.AsSpan())));
                if (result.Validation is not { Mismatches: 0, Unresolved: 0 } || result.UnresolvedGlitchCount != 0
                    || hash != old.GetProperty("hash").GetString()
                    || result.RepairedCount != old.GetProperty("RepairedCount").GetInt32()
                    || result.ReferencePasses != old.GetProperty("ReferencePasses").GetInt32()
                    || result.Float64GlitchCount != old.GetProperty("Float64GlitchCount").GetInt32())
                    throw new Exception("Production FMA smoke differs from prior experiment or MPFR validation.");
                File.WriteAllText(output, JsonSerializer.Serialize(new { phase = "passed", width = w, height = h, frames, prior,
                    hash, result.Validation, result.ReferencePasses, result.RepairedCount, result.Float64GlitchCount,
                    milliseconds = timer.Elapsed.TotalMilliseconds, instrumented = true,
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(MandelbrotRenderer).Assembly.Location))) }) + Environment.NewLine);
                Console.WriteLine($"Production FMA {w}x{h}: exact experimental image, {result.Validation}, {result.RepairedCount} repairs.");
            }
            finally { view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose(); }
        }
        finally { foreach (var doc in reports.Concat(starts)) doc.Dispose(); }
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
}
