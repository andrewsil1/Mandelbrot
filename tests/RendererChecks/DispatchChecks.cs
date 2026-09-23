using System.ComponentModel;
using System.Reflection;
using MandelbrotGpu;

internal static class DispatchChecks
{
    public static void Run()
    {
        Assembly assembly = typeof(MandelbrotRenderer).Assembly;
        MethodInfo batchSize = assembly.GetType("MandelbrotGpu.GpuDispatchPolicy")!.GetMethod("BatchPixels")!;
        MethodInfo lost = assembly.GetType("MandelbrotGpu.GpuDeviceFailure")!.GetMethod("TryGetCode")!;
        foreach (int code in new[] { unchecked((int)0x887A0005), unchecked((int)0x887A0006), unchecked((int)0x887A0007), unchecked((int)0x887A0020) })
        {
            object?[] arguments = [new InvalidOperationException("wrapper", new Win32Exception(code)), 0];
            if (!(bool)lost.Invoke(null, arguments)! || (int)arguments[1]! != code)
                throw new Exception("Device-loss classifier failed to preserve the native code.");
        }
        object?[] ordinary = [new ArgumentOutOfRangeException("groupsX"), 0];
        if ((bool)lost.Invoke(null, ordinary)!) throw new Exception("Ordinary programming error treated as device loss.");

        foreach (RenderMode mode in Enum.GetValues<RenderMode>())
        {
            int previous = int.MaxValue;
            foreach (int iterations in new[] { 1, 256, 6912, 32768 })
            {
                int count = (int)batchSize.Invoke(null, [mode, iterations])!;
                if (count < 64 || count % 64 != 0 || count > previous || count / 64 > 65535)
                    throw new Exception("Dispatch budget doesn't respect iteration/group limits.");
                previous = count;
            }
        }

        if (GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationFloat64, 128) != 131072
            || GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationDoubleDouble, 128) != 32768)
            throw new Exception("Production perturbation batch capacity changed.");
        foreach (int iterations in new[] { 128, 256, 1024, 6912, 32768 })
        {
            if ((long)GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationFloat64, iterations) * iterations > 128_000_000
                || (long)GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationDoubleDouble, iterations) * iterations > 16_000_000)
                throw new Exception("Wider batches exceeded the existing iteration-work ceiling.");
        }

        // Direct and FP64 outputs must retain the global pixel index across
        // submissions, including the non-group-aligned final tail.
        const int width = 521, height = 257, budget = 256;
        MandelbrotViewport viewport = MandelbrotViewport.FullSet(width, height);
        MethodInfo direct = typeof(MandelbrotRenderer).GetMethod("RenderDirect", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo fp = typeof(MandelbrotRenderer).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "RenderPerturbationFloat64" && m.GetParameters().Length == 3);
        MethodInfo mpfr = assembly.GetType("MandelbrotGpu.MpfrMandelbrot")!.GetMethod("EscapeIterations")!;
        FieldInfo timing = typeof(MandelbrotRenderer).GetField("timings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using MpfrComplex reference = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        foreach (RenderMode mode in new[] { RenderMode.DirectFloat64, RenderMode.PerturbationFloat64 })
        {
            MandelbrotRenderer renderer = new(width, height);
            int[] actual = mode == RenderMode.DirectFloat64
                ? (int[])direct.Invoke(renderer, [viewport.ToFloat64View(), budget])!
                : (int[])fp.Invoke(renderer, [viewport, reference, budget])!;
            int size = (int)batchSize.Invoke(null, [mode, budget])!;
            int expectedBatches = (actual.Length + size - 1) / size;
            int dispatches = ((RenderTimings)timing.GetValue(renderer)!).DispatchCount;
            int maximumSlices = mode == RenderMode.DirectFloat64 ? 1 : 2;
            if (dispatches < expectedBatches || dispatches > expectedBatches * maximumSlices || expectedBatches <= 1)
                throw new Exception("Multi-batch dispatch fixture did not cover the expected batches.");
            int[] samples = [0, size - 1, size, size + 1, actual.Length - 1];
            foreach (int index in samples)
            {
                using MpfrComplex point = viewport.PointAtPixel(index % width, index / width, width, height);
                int expected = (int)mpfr.Invoke(null, [point, budget, (uint)768])!;
                if (actual[index] != expected)
                    throw new Exception($"{mode}: pixel offset mismatch at batch seam {index}.");
            }
        }
        Console.WriteLine("Dispatch policy, direct/FP64 batch seams, and device-loss classification passed.");
    }
}
