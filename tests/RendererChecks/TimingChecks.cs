using MandelbrotGpu;

internal static class TimingChecks
{
    public static void CheckQuietAttribution()
    {
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_DIAGNOSTICS");
        Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", "0");
        using var center = new MpfrComplex(MpfrFloat.FromDouble(-0.46729724795644717, 384), MpfrFloat.FromDouble(0.5881255899137422, 384));
        var view = MandelbrotViewport.FullSet(17, 9).Zoom(center, Math.ScaleB(1, -40));
        try
        {
            var quiet = new MandelbrotRenderer(17, 9).Render(view, 6912);
            var measured = new MandelbrotRenderer(17, 9) { ProfileTimings = true }.Render(view, 6912);
            var t = measured.Timings;
            if (!quiet.Pixels.SequenceEqual(measured.Pixels) || measured.UnresolvedGlitchCount != 0
                || measured.Validation is not null || t.JournalWriteCount != 0 || t.TotalMilliseconds <= 0
                || t.OtherHostMilliseconds < -0.1 || t.Float64DispatchMilliseconds <= 0
                || Math.Abs(t.DispatchMilliseconds - t.SubmissionMilliseconds - t.CompletionWaitMilliseconds) > 0.1
                || Math.Abs(t.Float64DispatchMilliseconds - t.Float64SubmissionMilliseconds - t.Float64CompletionWaitMilliseconds) > 0.1
                || Math.Abs(t.DoubleDoubleDispatchMilliseconds - t.DoubleDoubleSubmissionMilliseconds - t.DoubleDoubleCompletionWaitMilliseconds) > 0.1)
                throw new Exception("No-journal attribution changed pixels or failed exclusive timing accounting.");
            Console.WriteLine("No-journal attribution passed: exact quiet image, exclusive per-mode submission/wait accounting.");
        }
        finally
        {
            view.CenterX.Dispose(); view.CenterY.Dispose(); view.Height.Dispose();
            Environment.SetEnvironmentVariable("MANDELBROT_DIAGNOSTICS", previous);
        }
    }

    public static void CheckConfiguration()
    {
        RenderTimings timing = new()
        {
            TotalMilliseconds = 100,
            ReferenceMilliseconds = 1, UploadMilliseconds = 2,
            DispatchMilliseconds = 3, ReadbackMilliseconds = 4,
            RepairMilliseconds = 5, ColoringMilliseconds = 6,
            BlaMilliseconds = 7, ValidationMilliseconds = 8,
            JournalMilliseconds = 9, SliceScanMilliseconds = 10,
            // Inclusive envelopes and journal breakdown must not be added again.
            Float64SliceLoopMilliseconds = 1000, DoubleDoubleSliceLoopMilliseconds = 2000,
            JournalFileMilliseconds = 6, JournalSerializationMilliseconds = 2,
            Float64JournalMilliseconds = 4, DoubleDoubleJournalMilliseconds = 5
        };
        if (timing.AccountedMilliseconds != 55 || timing.OtherHostMilliseconds != 45)
            throw new Exception("Exclusive timing accounting double-counted an inclusive timer.");
        timing.TotalMilliseconds = 50;
        if (timing.OtherHostMilliseconds != -5)
            throw new Exception("Timing overlap was hidden by clamping the residual.");
        Console.WriteLine("GPU-free timing checks passed: exclusive stages, inclusive envelopes, signed residual.");
    }

    public static void CheckResult(RenderResult result)
    {
        RenderTimings t = result.Timings;
        double[] values = [t.TotalMilliseconds, t.ReferenceMilliseconds, t.UploadMilliseconds,
            t.DispatchMilliseconds, t.Float64DispatchMilliseconds, t.DoubleDoubleDispatchMilliseconds,
            t.MaxDispatchMilliseconds, t.ReadbackMilliseconds, t.RepairMilliseconds,
            t.ColoringMilliseconds, t.BlaMilliseconds, t.ValidationMilliseconds,
            t.JournalMilliseconds, t.JournalSerializationMilliseconds, t.JournalFileMilliseconds,
            t.Float64JournalMilliseconds, t.DoubleDoubleJournalMilliseconds,
            t.SliceScanMilliseconds, t.Float64SliceLoopMilliseconds, t.DoubleDoubleSliceLoopMilliseconds,
            t.SubmissionMilliseconds, t.CompletionWaitMilliseconds];
        if (t.SubmissionMilliseconds < 0 || t.CompletionWaitMilliseconds < 0
            || t.Float64SubmissionMilliseconds + t.DoubleDoubleSubmissionMilliseconds > t.SubmissionMilliseconds + 0.1
            || t.Float64CompletionWaitMilliseconds + t.DoubleDoubleCompletionWaitMilliseconds > t.CompletionWaitMilliseconds + 0.1
            || t.SubmissionMilliseconds + t.CompletionWaitMilliseconds > t.DispatchMilliseconds + 0.1
            || t.AsyncDispatchCount > t.DispatchCount || t.MaxInFlightSubmissions > 2)
            throw new Exception("Asynchronous timing double-counted overlapping completion ages or exceeded its bound.");
        if (values.Any(value => !double.IsFinite(value) || value < 0)
            || t.TotalMilliseconds <= 0 || t.JournalMilliseconds <= 0
            || t.JournalWriteCount != 2 * (t.JournalGroupCount +
                (result.Mode == RenderMode.DirectFloat64 ? t.DispatchCount : 0))
            || t.JournalRecordCount != 2 * t.DispatchCount + 2 * t.JournalGroupCount
            || (result.Validation is not null && t.ValidationMilliseconds <= 0))
            throw new Exception("Render timing coverage/counters failed.");
        if (result.Mode != RenderMode.DirectFloat64
            && (t.SliceReadbackCount <= 0 || t.SkippedSliceReadbacks < 0
                || t.SliceReadbackCount + t.SkippedSliceReadbacks != t.DispatchCount
                || t.Float64SliceReadbackCount + t.DoubleDoubleSliceReadbackCount != t.SliceReadbackCount
                || t.SliceReadbackBytes <= 0))
            throw new Exception("Slice readback coverage/counters failed.");
        // Small rounding allowance only; no hardware-dependent speed assertion.
        const double tolerance = 0.1;
        if (t.OtherHostMilliseconds < -tolerance
            || t.JournalFileMilliseconds + t.JournalSerializationMilliseconds > t.JournalMilliseconds + tolerance
            || t.Float64DispatchMilliseconds + t.Float64JournalMilliseconds > t.Float64SliceLoopMilliseconds + tolerance
            || t.DoubleDoubleDispatchMilliseconds + t.DoubleDoubleJournalMilliseconds > t.DoubleDoubleSliceLoopMilliseconds + tolerance
            || t.Float64SliceLoopMilliseconds + t.DoubleDoubleSliceLoopMilliseconds > t.TotalMilliseconds + tolerance)
            throw new Exception("Render timing intervals overlap or exceed their parent envelope.");
    }
}
