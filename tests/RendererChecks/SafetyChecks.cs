using System.Reflection;
using System.Text.Json;
using MandelbrotGpu;

internal static class SafetyChecks
{
    public static void Run()
    {
        Assembly assembly = typeof(MandelbrotRenderer).Assembly;
        Type ownership = assembly.GetType("MandelbrotGpu.ResourceOwnership")!;
        MethodInfo own = ownership.GetMethod("Own")!.MakeGenericMethod(typeof(IDisposable));
        List<int> disposed = [];
        IDisposable workspace = (IDisposable)Activator.CreateInstance(ownership)!;
        for (int index = 0; index < 3; index++)
            own.Invoke(workspace, [new Probe(index, disposed, index == 1)]);
        // A cleanup exception must not prevent releasing the other allocations.
        try { workspace.Dispose(); throw new Exception("Expected cleanup failure missing."); }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1) { }
        if (!disposed.SequenceEqual(new[] { 2, 1, 0 }))
            throw new Exception("Partial workspace cleanup order/coverage failed.");
        workspace.Dispose();
        if (disposed.Count != 3) throw new Exception("Workspace cleanup was not idempotent.");

        // Simulate failure after the first successful allocation, without a GPU.
        using (IDisposable partial = (IDisposable)Activator.CreateInstance(ownership)!)
        {
            own.Invoke(partial, [new Probe(3, disposed, false)]);
        }
        if (disposed[^1] != 3) throw new Exception("Partial allocation was not released.");

        Type journal = assembly.GetType("MandelbrotGpu.DispatchJournal")!;
        MethodInfo brake = journal.GetMethod("ShouldSuspend")!;
        if ((bool)brake.Invoke(null, [999.9])! || !(bool)brake.Invoke(null, [1000.0])!)
            throw new Exception("Long-dispatch brake boundary failed.");
        Guid submission = Guid.NewGuid();
        object writeTiming = journal.GetMethod("Write")!.Invoke(null, [new { phase = "test", submission }])!;
        double ReadTiming(string name) => (double)writeTiming.GetType().GetProperty(name)!.GetValue(writeTiming)!;
        double total = ReadTiming("TotalMilliseconds");
        double serialization = ReadTiming("SerializationMilliseconds");
        double file = ReadTiming("FileMilliseconds");
        if (!double.IsFinite(total) || serialization < 0 || file < 0
            || serialization + file > total + 0.001)
            throw new Exception("Journal timing breakdown failed.");
        string path = (string)journal.GetProperty("LogPath")!.GetValue(null)!;
        using JsonDocument last = JsonDocument.Parse(File.ReadLines(path).Last());
        if (last.RootElement.GetProperty("submission").GetGuid() != submission)
            throw new Exception("Dispatch journal readback failed.");
        MethodInfo sliceSize = assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!.GetMethod("SliceIterations")!;
        string? originalSlice = Environment.GetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", null);
            if ((int)sliceSize.Invoke(null, [257])! != 128 || (int)sliceSize.Invoke(null, [1])! != 1)
                throw new Exception("Default slice size/budget clamp failed.");
            foreach (string invalid in new[] { "-1", "32769", "invalid" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", invalid);
                try { sliceSize.Invoke(null, [257]); throw new Exception("Invalid slice accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "0");
            if ((int)sliceSize.Invoke(null, [257])! != 257) throw new Exception("Unsliced comparison mode failed.");
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", originalSlice); }
        Type policy = assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!;
        MethodInfo readbackSlices = policy.GetMethod("ReadbackSlices")!;
        MethodInfo shouldRead = policy.GetMethod("ShouldReadSlice")!;
        string? originalReadback = Environment.GetEnvironmentVariable("MANDELBROT_READBACK_SLICES");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", null);
            if ((int)readbackSlices.Invoke(null, null)! != 4) throw new Exception("Default readback interval failed.");
            foreach (int interval in new[] { 1, 4, 8 })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", interval.ToString());
                if ((int)readbackSlices.Invoke(null, null)! != interval) throw new Exception("Readback interval parsing failed.");
                for (int completed = 1; completed <= 17; completed++)
                    if ((bool)shouldRead.Invoke(null, [completed, interval, completed == 17])!
                        != (completed == 1 || completed % interval == 0 || completed == 17))
                        throw new Exception("First, periodic, or final readback scheduling failed.");
            }
            foreach (string invalid in new[] { "0", "9", "-1", "invalid" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", invalid);
                try { readbackSlices.Invoke(null, null); throw new Exception("Invalid readback interval accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", originalReadback); }
        Console.WriteLine("GPU-free delayed-readback checks passed: interval bounds, first/periodic/final scheduling.");
        Console.WriteLine("GPU-free safety checks passed: reverse cleanup, cleanup failure, idempotence, dispatch brake, durable journal.");
    }

    private sealed class Probe(int index, List<int> disposed, bool fail) : IDisposable
    {
        public void Dispose()
        {
            disposed.Add(index);
            if (fail) throw new InvalidOperationException("Injected cleanup failure.");
        }
    }
}
