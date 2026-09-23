using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using ComputeSharp;
using MandelbrotGpu;

internal static class AllocationChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string? profile = null)
    {
        CheckReferences();
        CheckColoring();
        CheckSparseCapacity();
        CheckViewportOwnership();
        if (profile is not null) ProfileReferences(profile);
        Console.WriteLine("Allocation checks passed: bitwise reference/BLA equivalence, in-place colors, sparse capacity, viewport retirement and active-render snapshots.");
    }

    private static bool Same(double[] a, double[] b) =>
        MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(b.AsSpan()));

    private static void CheckReferences()
    {
        foreach (var (x, y) in new[] { (0.0, 0.0), (4.0, 0.0), (-2.0, 0.0), (0.0, 1.0),
            (-0.743643887037151, 0.13182590420533), (-0.46729724795644717, 0.5881255899137422) })
        foreach (double delta in new[] { 0.0, 1E-28, -1E-60 })
        {
            using var real = MpfrFloat.FromDouble(x, 384);
            using var center = new MpfrComplex(real.Add(delta), MpfrFloat.FromDouble(y, 384));
            foreach (int budget in new[] { 0, 1, 512, 6912 })
            {
                var old = AllocatingReferenceBaseline.Build(center, budget);
                var current = DeepZoomReferenceOrbit.Build(center, budget);
                if (old.Length != current.Length || !Same(old.RealHigh, current.RealHigh) || !Same(old.RealLow, current.RealLow)
                    || !Same(old.ImaginaryHigh, current.ImaginaryHigh) || !Same(old.ImaginaryLow, current.ImaginaryLow))
                    throw new Exception($"Reference changed at ({x},{y}) + {delta}, budget {budget}.");
                // Feed the frozen reference arrays into the production BLA builder.
                var ctor = typeof(DeepZoomReferenceOrbit).GetConstructors(Private).Single();
                var oldOrbit = (DeepZoomReferenceOrbit)ctor.Invoke([old.RealHigh, old.RealLow, old.ImaginaryHigh, old.ImaginaryLow]);
                typeof(DeepZoomReferenceOrbit).GetProperty("Length")!.SetValue(oldOrbit, old.Length);
                if (!Same(new BlaTable(oldOrbit, 1E-12, 1E-18).Data, new BlaTable(current, 1E-12, 1E-18).Data))
                    throw new Exception("Reference reuse changed BLA entries.");
            }
        }
    }

    private static void CheckColoring()
    {
        foreach (int[] input in new int[][] { [], [-1, -2, -3], [0, 1, 1, 8, 32, -1, -2] })
        {
            var expected = HistogramColorizer.Colorize(input, 32, out var expectedPalette);
            var storage = (int[])input.Clone();
            var actual = HistogramColorizer.ColorizeInPlace(storage, 32, out var actualPalette);
            if (!ReferenceEquals(storage, actual) || !expected.SequenceEqual(actual) || !expectedPalette.SequenceEqual(actualPalette))
                throw new Exception("In-place colors changed or allocated replacement storage.");
        }
    }

    private static void CheckSparseCapacity()
    {
        const int width = 97, height = 53, budget = 256;
        using var full = MandelbrotViewport.FullSet(width, height);
        using var center = new MpfrComplex(MpfrFloat.FromDouble(-2, 384), MpfrFloat.FromDouble(0, 384));
        using var view = full.Zoom(center, 1E-28);
        var renderer = new MandelbrotRenderer(width, height);
        var method = typeof(MandelbrotRenderer).GetMethods(Private)
            .Single(m => m.Name == "RenderPerturbationDoubleDouble" && m.GetParameters().Length == 5);
        using var fullBuffers = new PerturbationBuffers(GraphicsDevice.GetDefault(), width * height, budget);
        foreach (int capacity in new[] { 1, 65, 137 })
        {
            using var small = new PerturbationBuffers(GraphicsDevice.GetDefault(), capacity, budget);
            if (small.Output.Length != capacity || small.PixelIndices.Length != capacity)
                throw new Exception("Sparse buffer capacity differs from requested tail.");
            foreach (int count in new[] { capacity, 1, capacity })
            {
                int[] indices = Enumerable.Range(0, count).Select(i => width * height - 1 - i * 7).ToArray();
                var expected = (int[])method.Invoke(renderer, [view, center, budget, indices, fullBuffers])!;
                var actual = (int[])method.Invoke(renderer, [view, center, budget, indices, small])!;
                if (!expected.SequenceEqual(actual)) throw new Exception("Right-sized sparse dispatch/reuse changed classifications.");
            }
        }
    }

    private static void CheckViewportOwnership()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                window = new MainWindow();
                object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
                object? Call(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
                void Retired(MandelbrotViewport v)
                {
                    foreach (var value in new[] { v.CenterX, v.CenterY, v.Height })
                        if (!(bool)typeof(MpfrFloat).GetField("disposed", Private)!.GetValue(value)!)
                            throw new Exception("Discarded viewport still owns MPFR storage.");
                }
                void Pump(Task task)
                {
                    var frame = new DispatcherFrame();
                    var timeout = Stopwatch.StartNew();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
                    timer.Tick += (_, _) => { if (task.IsCompleted || timeout.Elapsed.TotalSeconds > 30) frame.Continue = false; };
                    timer.Start();
                    try { Dispatcher.PushFrame(frame); }
                    finally { timer.Stop(); }
                    if (!task.IsCompleted) throw new TimeoutException("Queued viewport test did not finish.");
                    task.GetAwaiter().GetResult();
                    if (!(bool)Field("isClosed")! && !(bool)Field("isRendering")!
                        && !((TextBlock)window.FindName("StatusText")).Text.Contains("unresolved="))
                        throw new Exception("UI render swallowed a failure.");
                }
                var initial = (MandelbrotViewport)Field("view")!;
                Call("EnsureRenderTarget", true);
                Retired(initial);
                var active = (MandelbrotViewport)Field("view")!;
                Task rendering = (Task)Call("RenderAsync")!;
                // Dispose the source while its independently owned render snapshot runs.
                Call("ReplaceView", active.WithAspect(1.25));
                Retired(active);
                Pump((Task)Call("RenderAsync")!); // queues, does not overlap
                Pump(rendering);
                if ((bool)Field("isRendering")! || (bool)Field("renderQueued")!) throw new Exception("Render pump did not drain.");
                var stack = (Stack<MandelbrotViewport>)Field("history")!;
                var saved = MandelbrotViewport.FullSet(64, 64);
                stack.Push(saved);
                var replaced = (MandelbrotViewport)Field("view")!;
                Call("BackButton_Click", null, null);
                Retired(saved); Retired(replaced);
                Pump((Task)Call("RenderAsync")!);
                // Wait for the async-void Back handler's original render pump.
                while ((bool)Field("isRendering")!) Pump(Task.Delay(10));
                var resetHistory = MandelbrotViewport.FullSet(64, 64);
                stack.Push(resetHistory);
                var beforeReset = (MandelbrotViewport)Field("view")!;
                Call("ResetButton_Click", null, null);
                Retired(resetHistory); Retired(beforeReset);
                while ((bool)Field("isRendering")!) Pump(Task.Delay(10));
                var closeHistory = MandelbrotViewport.FullSet(64, 64);
                stack.Push(closeHistory);
                var closeView = (MandelbrotViewport)Field("view")!;
                Task closingRender = (Task)Call("RenderAsync")!;
                window.Close();
                Retired(closeHistory); Retired(closeView);
                Pump(closingRender);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ProfileReferences(string output)
    {
        if (File.Exists(output)) throw new IOException("Refusing to overwrite allocation profile.");
        using var log = new StreamWriter(output) { AutoFlush = true };
        using var c = new MpfrComplex(MpfrFloat.FromDouble(-0.46729724795644717, 384), MpfrFloat.FromDouble(0.5881255899137422, 384));
        for (int round = -1; round < 6; round++)
        foreach (bool allocating in (round % 2 == 0 ? new[] { true, false } : new[] { false, true }))
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 24; i++)
            {
                if (allocating) GC.KeepAlive(AllocatingReferenceBaseline.Build(c, 6912));
                else GC.KeepAlive(DeepZoomReferenceOrbit.Build(c, 6912));
            }
            timer.Stop();
            bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
            log.WriteLine(JsonSerializer.Serialize(new { round, allocating, repeats = 24, milliseconds = timer.Elapsed.TotalMilliseconds, managedBytes = bytes }));
        }
    }
}
