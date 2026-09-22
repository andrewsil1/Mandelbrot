using System.Diagnostics;
using System.Text.Json;
using ComputeSharp;
using MandelbrotGpu;

internal static class InteractiveChecks
{
    public static string[] Exact(MpfrFloat value) => value.ToExactBinary64Terms();

    public static void Run(int width, int height, string path)
    {
        Environment.SetEnvironmentVariable("MANDELBROT_VALIDATE", "1");
        Environment.SetEnvironmentVariable("MANDELBROT_METRICS", null);
        Environment.SetEnvironmentVariable("MANDELBROT_ACCELERATION", "bla");
        Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
        Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", "4");
        Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using StreamWriter output = new(path, append: true) { AutoFlush = true };
        void Write(object item) => output.WriteLine(JsonSerializer.Serialize(item));
        using var process = Process.GetCurrentProcess();
        foreach (var fixture in new[] { "transition", "tip" })
        {
            using MpfrComplex center = new(MpfrFloat.FromDouble(fixture == "tip" ? -2 : -0.67323438570448868, 384),
                MpfrFloat.FromDouble(fixture == "tip" ? 0 : 0.35743485497289235, 384));
            MandelbrotViewport view = MandelbrotViewport.FullSet(width, height).Zoom(center, fixture == "tip" ? 1E-28 : Math.ScaleB(1, -40));
            foreach (string action in new[] { "base", "zoom-in", "pan", "resize-wide", "zoom-out", "resize-restore" })
            {
                int w = action == "resize-wide" || action == "zoom-out" ? width + width / 8 : width;
                if (action == "zoom-in" || action == "zoom-out" || action == "pan")
                {
                    using MpfrComplex point = view.PointAt(action == "pan" ? 0.57 : 0.5, action == "pan" ? 0.46 : 0.5);
                    var next = view.Zoom(point, action == "zoom-in" ? 0.25 : action == "zoom-out" ? 4 : 1);
                    Dispose(view); view = next;
                }
                if (action.StartsWith("resize")) { var next = view.WithAspect((double)w / height); Dispose(view); view = next; }
                int budget = fixture == "tip" ? 4096 : action is "zoom-in" or "pan" or "resize-wide" ? 7232 : 6912;
                var coordinates = new { encoding = "exact sum of binary64 hex bit patterns", x = Exact(view.CenterX), y = Exact(view.CenterY), height = Exact(view.Height), aspectBits = BitConverter.DoubleToUInt64Bits(view.Aspect).ToString("X16"), precision = 384 };
                Write(new { phase = "started", fixture, action, width = w, height, budget, coordinates, utc = DateTime.UtcNow });
                process.Refresh(); var cpu = process.TotalProcessorTime; long privateBefore = process.PrivateMemorySize64;
                var watch = Stopwatch.StartNew();
                RenderResult result = new MandelbrotRenderer(w, height).Render(view, budget);
                watch.Stop(); process.Refresh();
                bool passed = result.UnresolvedGlitchCount == 0 && result.Validation is { Mismatches: 0, Unresolved: 0 };
                Write(new { phase = "completed", fixture, action, width = w, height, budget, coordinates, passed,
                    latencyMs = watch.Elapsed.TotalMilliseconds, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    privateBefore, privateAfter = process.PrivateMemorySize64, workingSet = process.WorkingSet64,
                    processLifetimePeakWorkingSet = process.PeakWorkingSet64, handles = process.HandleCount,
                    adapter = GraphicsDevice.GetDefault().Name, result.Float64GlitchCount, result.UsedDoubleDoubleFallback,
                    mode = result.Mode.ToString(), result.ReferencePasses, result.RepairedCount, result.UnresolvedGlitchCount,
                    result.FinalRepairLimit, repairIterationUpperBound = (long)result.RepairedCount * budget, result.Timings, result.Validation });
                Console.WriteLine($"{fixture}/{action} {w}x{height}: {watch.Elapsed.TotalMilliseconds:F1} ms, refs={result.ReferencePasses}, repaired={result.RepairedCount}, unresolved={result.UnresolvedGlitchCount}, passed={passed}");
                if (!passed) throw new Exception("Interactive stage failed; stop escalation.");
            }
            Dispose(view);
        }
    }

    private static void Dispose(MandelbrotViewport view) { view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose(); }
}

