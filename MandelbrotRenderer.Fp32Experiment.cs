using ComputeSharp;

namespace MandelbrotGpu;

public sealed partial class MandelbrotRenderer
{
    internal bool ExperimentalFp32 { get; init; }
    internal int ExperimentalAccepted { get; private set; }

    // Fixed research envelope, deliberately no application/environment switch.
    // Below the normal scale envelope, hand every pixel to existing recovery.
    internal int[] RenderExperimentalFp32(MandelbrotViewport viewport, int budget)
    {
        if (width > 1024 || height > 1024 || budget > 32768 || budget < 1)
            throw new ArgumentOutOfRangeException(nameof(budget), "FP32 experiment is limited to 1024x1024 and 32768 iterations.");
        using MpfrComplex center = new(viewport.CenterX.Clone(), viewport.CenterY.Clone());
        GetFloat64Deltas(viewport, center, out double left, out double top, out double sx, out double sy);
        double bound = Math.Max(Math.Abs(left), Math.Max(Math.Abs(top), Math.Max(Math.Abs(sx), Math.Abs(sy))));
        double scale = Math.ScaleB(1, Math.ILogB(bound));
        int count = width * height;
        int[] result = new int[count];
        if (!double.IsFinite(scale) || scale < 1E-30 || scale > 1 || bound <= 0)
        {
            Array.Fill(result, EscapeClassification.Glitch);
            return result;
        }
        DiagnosticTimer timer = DiagnosticTimer.StartNew(MeasureTimings);
        var orbit = DeepZoomReferenceOrbit.Build(center, budget);
        timings.ReferenceMilliseconds += timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        using var output = device.AllocateReadWriteBuffer<int>(count);
        using var real = device.AllocateReadOnlyBuffer(orbit.RealHigh.Select(v => (float)v).ToArray());
        using var imaginary = device.AllocateReadOnlyBuffer(orbit.ImaginaryHigh.Select(v => (float)v).ToArray());
        int slices = GpuDispatchPolicy.SliceIterations(budget);
        int batch = GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationFloat64, slices);
        using var state = device.AllocateReadWriteBuffer<float>(Math.Min(count, batch) * 8);
        using var metrics = device.AllocateReadWriteBuffer<int>(1);
        using var readback = device.AllocateReadBackBuffer<int>(Math.Min(count, batch));
        int inFlight = GpuDispatchPolicy.InFlightSubmissions();
        using GpuDeviceStatus? status = inFlight > 1 ? new(device) : null;
        timings.UploadMilliseconds += timer.Elapsed.TotalMilliseconds;
        BeginPass(budget, diagnostics ? new { experiment = "rescaled-fp32-scalar", stateBytes = state.Length * 4 } : null, 1);
        timer.Restart();
        for (int offset = 0; offset < count; offset += batch)
        {
            int n = Math.Min(batch, count - offset), completed = 0;
            using var journal = CreateJournalGroup(RenderMode.PerturbationFloat64, budget, slices);
            using BoundedGpuQueue queue = new(inFlight, status is null ? null : status.Check) { MeasureWait = MeasureTimings };
            for (int start = 0; start < budget; start += slices)
            {
                int end = Math.Min(budget, start + slices);
                Dispatch(RenderMode.PerturbationFloat64, offset, n,
                    new ExperimentalFp32Shader(output, real, imaginary, state, (float)scale,
                        (float)(left / scale), (float)(top / scale), (float)(sx / scale), (float)(sy / scale),
                        orbit.Length, width, offset, start, end, budget), start, end, journal,
                    output, null, metrics, inFlight > 1 ? queue : null, state);
                bool pending = true;
                if (GpuDispatchPolicy.ShouldReadSlice(++completed, GpuDispatchPolicy.ReadbackSlices(), end == budget))
                {
                    queue.Drain();
                    pending = ReadSlice(output, readback, result.AsSpan(offset, n), offset, RenderMode.PerturbationFloat64);
                }
                else if (MeasureTimings) timings.SkippedSliceReadbacks++;
                if (journal is { HasPending: false }) journal.Checkpoint(!pending || end == budget);
                if (!pending) break;
            }
        }
        timings.Float64SliceLoopMilliseconds += timer.Elapsed.TotalMilliseconds;
        EnsureComplete(result);
        return result;
    }
}
