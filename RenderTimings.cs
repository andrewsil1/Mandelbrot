namespace MandelbrotGpu;

// These are host wall-clock measurements, not GPU timestamp queries or measured
// GPU utilization. Dispatch is exclusive host recording/submission plus waits;
// asynchronous submission-to-observed-completion ages can overlap and are not summed.
public sealed class RenderTimings
{
    public double ReferenceMilliseconds { get; set; }
    public double UploadMilliseconds { get; set; }
    public double DispatchMilliseconds { get; set; }
    public double Float64DispatchMilliseconds { get; set; }
    public double DoubleDoubleDispatchMilliseconds { get; set; }
    public int DispatchCount { get; set; }
    public long DoubleDoubleDispatchedThreads { get; set; }
    public double MaxDispatchMilliseconds { get; set; }
    public double SubmissionMilliseconds { get; set; }
    public double CompletionWaitMilliseconds { get; set; }
    public double Float64SubmissionMilliseconds { get; set; }
    public double DoubleDoubleSubmissionMilliseconds { get; set; }
    public double Float64CompletionWaitMilliseconds { get; set; }
    public double DoubleDoubleCompletionWaitMilliseconds { get; set; }
    public int AsyncDispatchCount { get; set; }
    public int MaxInFlightSubmissions { get; set; }
    public double ReadbackMilliseconds { get; set; }
    public int SliceReadbackCount { get; set; }
    public int Float64SliceReadbackCount { get; set; }
    public int DoubleDoubleSliceReadbackCount { get; set; }
    public int SkippedSliceReadbacks { get; set; }
    public long SliceReadbackBytes { get; set; }
    public double RepairMilliseconds { get; set; }
    public long RepairIterations { get; set; }
    public double ColoringMilliseconds { get; set; }
    public double BlaMilliseconds { get; set; }
    public double TotalMilliseconds { get; set; }
    public double ValidationMilliseconds { get; set; }
    public double JournalMilliseconds { get; set; }
    public double JournalSerializationMilliseconds { get; set; }
    public double JournalFileMilliseconds { get; set; }
    public int JournalWriteCount { get; set; }
    public int JournalRecordCount { get; set; }
    public int JournalGroupCount { get; set; }
    public double Float64JournalMilliseconds { get; set; }
    public double DoubleDoubleJournalMilliseconds { get; set; }
    public double SliceScanMilliseconds { get; set; }
    // Inclusive loop envelopes allow per-mode comparison with monitor readings.
    // Do not add them (or journal subcategories) to the exclusive stage total.
    public double Float64SliceLoopMilliseconds { get; set; }
    public double DoubleDoubleSliceLoopMilliseconds { get; set; }
    public double AccountedMilliseconds => ReferenceMilliseconds + UploadMilliseconds
        + DispatchMilliseconds + ReadbackMilliseconds + RepairMilliseconds
        + ColoringMilliseconds + BlaMilliseconds + ValidationMilliseconds
        + JournalMilliseconds + SliceScanMilliseconds;
    // Residual includes allocation/cleanup, coordinate conversion, sparse-map
    // construction/merging, shader construction, and uninstrumented host work.
    // Leave it signed so an overlapping timer is visible rather than masked.
    public double OtherHostMilliseconds => TotalMilliseconds - AccountedMilliseconds;
    public long Rebases { get; set; }
    public long SkippedIterations { get; set; }
    public long ScalarIterations { get; set; }
    public long PerturbationPixelEvaluations { get; set; }

    public override string ToString() =>
        $"Reference: {ReferenceMilliseconds:n1} ms; BLA build: {BlaMilliseconds:n1} ms; upload: {UploadMilliseconds:n1} ms; " +
        $"dispatch: {DispatchMilliseconds:n1} ms (FP64: {Float64DispatchMilliseconds:n1}, DD: {DoubleDoubleDispatchMilliseconds:n1}); readback/wait: {ReadbackMilliseconds:n1} ms; " +
        $"slice readbacks: {SliceReadbackCount:n0} (FP64/DD: {Float64SliceReadbackCount:n0}/{DoubleDoubleSliceReadbackCount:n0}, skipped: {SkippedSliceReadbacks:n0}, bytes: {SliceReadbackBytes:n0}); " +
        $"repair: {RepairMilliseconds:n1} ms; color: {ColoringMilliseconds:n1} ms; " +
        $"journal: {JournalMilliseconds:n1} ms (serialization: {JournalSerializationMilliseconds:n1}, file: {JournalFileMilliseconds:n1}, writes: {JournalWriteCount:n0}, groups: {JournalGroupCount:n0}, records: {JournalRecordCount:n0}); " +
        $"slice scan: {SliceScanMilliseconds:n1} ms; validation: {ValidationMilliseconds:n1} ms; other host: {OtherHostMilliseconds:n1} ms; total: {TotalMilliseconds:n1} ms; " +
        $"slice loops FP64/DD: {Float64SliceLoopMilliseconds:n1}/{DoubleDoubleSliceLoopMilliseconds:n1} ms; " +
        $"batches: {DispatchCount:n0}, longest: {MaxDispatchMilliseconds:n1} ms; " +
        $"DD dispatched threads: {DoubleDoubleDispatchedThreads:n0}; " +
        $"async: {AsyncDispatchCount:n0}, peak in-flight: {MaxInFlightSubmissions}, submit/wait: {SubmissionMilliseconds:n1}/{CompletionWaitMilliseconds:n1} ms; " +
        $"perturbation pixel evaluations: {PerturbationPixelEvaluations:n0}" +
        (Environment.GetEnvironmentVariable("MANDELBROT_METRICS") == "1"
            ? $"; rebases: {Rebases:n0}; skipped iterations: {SkippedIterations:n0}; scalar iterations: {ScalarIterations:n0}" : string.Empty);
}
