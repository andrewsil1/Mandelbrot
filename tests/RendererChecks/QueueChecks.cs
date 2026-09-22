using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using ComputeSharp;
using MandelbrotGpu;

internal static class QueueChecks
{
    private static readonly Assembly Assembly = typeof(MandelbrotRenderer).Assembly;
    private static readonly Type QueueType = Assembly.GetType("MandelbrotGpu.BoundedGpuQueue")!;
    private static object Queue(Action? check = null) => Activator.CreateInstance(QueueType, [2, check])!;
    private static void Call(object queue, string method, params object[] args) => QueueType.GetMethod(method)!.Invoke(queue, args);
    private static int Count(object queue) => (int)QueueType.GetProperty("Count")!.GetValue(queue)!;
    private static void Add(object queue, Task task, Action<double> complete, Action<Exception>? fail = null) =>
        Call(queue, "Add", task, complete, fail ?? (_ => { }));

    public static void CheckConfiguration()
    {
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_INFLIGHT");
        MethodInfo policy = Assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!.GetMethod("InFlightSubmissions")!;
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", null);
            if ((int)policy.Invoke(null, null)! != 2) throw new Exception("Default queue capacity failed.");
            foreach (int depth in new[] { 1, 2 })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", depth.ToString());
                if ((int)policy.Invoke(null, null)! != depth) throw new Exception("Queue capacity mapping failed.");
            }
            foreach (string invalid in new[] { "0", "3", "-1", "invalid" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", invalid);
                try { policy.Invoke(null, null); throw new Exception("Unbounded queue setting accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", previous); }

        CheckCompletionTiming();

        List<int> completed = [];
        object queue = Queue();
        using ((IDisposable)queue)
        {
            Add(queue, Task.CompletedTask, _ => completed.Add(1));
            Add(queue, Task.CompletedTask, _ => completed.Add(2));
            try { Add(queue, Task.CompletedTask, _ => { }); throw new Exception("Queue exceeded capacity."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            Call(queue, "MakeRoom");
            if (Count(queue) != 1 || !completed.SequenceEqual(new[] { 1 })) throw new Exception("FIFO/backpressure failed.");
            Add(queue, Task.CompletedTask, _ => completed.Add(3));
            Call(queue, "Drain");
            if (Count(queue) != 0 || !completed.SequenceEqual(new[] { 1, 2, 3 })) throw new Exception("Drain lost completion.");
        }
        ((IDisposable)queue).Dispose();
        foreach (bool deviceFailure in new[] { false, true })
        {
            Exception original = new Win32Exception(unchecked((int)0x887A0005));
            queue = Queue(deviceFailure ? () => throw original : null);
            using ((IDisposable)queue)
            {
                Exception? reported = null;
                Add(queue, deviceFailure ? Task.CompletedTask : Task.FromException(original), _ => throw new Exception("False completion."), ex => reported = ex);
                Task tail = Task.Delay(20);
                Add(queue, tail, _ => throw new Exception("Tail falsely logged complete after failure."));
                try { Call(queue, "Drain"); throw new Exception("Queue failure ignored."); }
                catch (TargetInvocationException ex) when (ReferenceEquals(ex.InnerException, original)) { }
                if (!ReferenceEquals(reported, original) || !tail.IsCompleted || Count(queue) != 0)
                    throw new Exception("Failure replaced original or freed resources before issued tail completed.");
                try { Call(queue, "MakeRoom"); throw new Exception("Stopped queue resumed."); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            }
        }
        MethodInfo reason = Assembly.GetType("MandelbrotGpu.GpuDeviceStatus")!.GetMethod("CheckReason")!;
        reason.Invoke(null, [0]);
        try { reason.Invoke(null, [unchecked((int)0x887A0005)]); throw new Exception("Removal fence falsely completed."); }
        catch (TargetInvocationException ex) when (ex.InnerException is Win32Exception) { }
        Console.WriteLine("GPU-free bounded queue checks passed: capacities, FIFO, backpressure, drain, failure/tail lifetime, device-loss status.");
    }

    private static void CheckCompletionTiming()
    {
        MethodInfo brake = Assembly.GetType("MandelbrotGpu.DispatchJournal")!.GetMethod("ShouldSuspend")!;
        foreach (bool genuinelySlow in new[] { false, true })
        {
            long now = 0;
            long Tick(double ms) => (long)Math.Round(ms * Stopwatch.Frequency / 1000);
            object queue = Queue();
            QueueType.GetProperty("Timestamp")!.SetValue(queue, (Func<long>)(() => Volatile.Read(ref now)));
            QueueType.GetProperty("MeasureWait")!.SetValue(queue, false);
            using ((IDisposable)queue)
            {
                // Simulate five seconds of shader/pipeline recording before
                // submission, then two seconds of host work after completion.
                now = Tick(5000);
                long submitted = now;
                TaskCompletionSource<bool> source = new();
                double duration = -1;
                Action<double, double> complete = (wait, latency) =>
                {
                    if (wait != 0) throw new Exception("Disabled wait diagnostics still measured a duration.");
                    duration = latency;
                };
                Call(queue, "AddTimed", source.Task, submitted, complete, (Action<Exception>)(_ => { }));
                double expected = genuinelySlow ? 1094 : 20;
                now = submitted + Tick(expected);
                source.SetResult(true); // ExecuteSynchronously captures this clock value.
                now += Tick(2000);
                Call(queue, "Drain");
                if (Math.Abs(duration - expected) > 0.001
                    || (bool)brake.Invoke(null, [duration])! != genuinelySlow)
                    throw new Exception($"Completion safety timing includes recording/late consumption or misses a slow submission: {duration}.");
            }
        }
        Console.WriteLine("GPU-free completion timing passed: cold recording and late FIFO consumption excluded; genuine 1094ms completion still trips the unchanged brake.");
    }

    public static void CheckRendering()
    {
        string? original = Environment.GetEnvironmentVariable("MANDELBROT_INFLIGHT");
        string? readback = Environment.GetEnvironmentVariable("MANDELBROT_READBACK_SLICES");
        string? grouping = Environment.GetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES");
        try
        {
            // Native status export, healthy reason, and idempotent COM release.
            using IDisposable status = (IDisposable)Activator.CreateInstance(Assembly.GetType("MandelbrotGpu.GpuDeviceStatus")!, [GraphicsDevice.GetDefault()])!;
            status.GetType().GetMethod("Check")!.Invoke(status, null);
            status.Dispose();
            status.Dispose();
            try { status.GetType().GetMethod("Check")!.Invoke(status, null); throw new Exception("Released native pointer reused."); }
            catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException) { }
            foreach (string interval in new[] { "1", "4", "8" })
            foreach (string group in new[] { "1", "8" })
            foreach (double scale in new[] { Math.ScaleB(1, -40), 1E-28 })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", interval);
                Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", group);
                using MpfrComplex center = new(MpfrFloat.FromDouble(-0.34562335012588691, 384), MpfrFloat.FromDouble(0.625450590999726, 384));
                MandelbrotViewport view = MandelbrotViewport.FullSet(13, 9).Zoom(center, scale);
                Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "1");
                RenderResult synchronous = PrecisionProfilingChecks.Render(new(13, 9), view, 6912, scale < 1E-26);
                Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
                RenderResult queued = PrecisionProfilingChecks.Render(new(13, 9), view, 6912, scale < 1E-26);
                TimingChecks.CheckResult(queued);
                if (!synchronous.Pixels.SequenceEqual(queued.Pixels) || synchronous.RepairedCount != queued.RepairedCount
                    || synchronous.UnresolvedGlitchCount != queued.UnresolvedGlitchCount
                    || synchronous.Timings.DispatchCount != queued.Timings.DispatchCount
                    || queued.Validation is not { Mismatches: 0, Unresolved: 0 }
                    || queued.Timings.AsyncDispatchCount != queued.Timings.DispatchCount
                    || queued.Timings.MaxInFlightSubmissions is < 1 or > 2)
                    throw new Exception("Queued pipeline changed output/work count or exceeded bound.");
                if (group == "8" && interval != "1" && queued.Timings.MaxInFlightSubmissions != 2)
                    throw new Exception("Queue fixture did not overlap submissions.");
            }
            // Existing raw MPFR, arithmetic-counter, batch-seam, and sparse
            // reference-reuse checks now exercise the asynchronous path too.
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
            Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", "8");
            SliceChecks.Run();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", original);
            Environment.SetEnvironmentVariable("MANDELBROT_READBACK_SLICES", readback);
            Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", grouping);
        }
        Console.WriteLine("Bounded queue validation passed: FP64/DD exact images/dispatches, MPFR, barriers, readback/group bounds, native status, raw seams/reuse.");
    }
}
