namespace MandelbrotGpu;

// GPU counters are explicit diagnostic opt-in, never quiet Release collection.
internal sealed class BlaPassProfile
{
    public const int CounterCount = 18;
    public const int MetricStride = 3 + CounterCount;
    public static readonly string[] CounterNames = ["searches", "candidates", "zeroRadius", "deltaRejected",
        "errorRejected", "acceptedBlocks", "sliceLimited", "alignmentStops", "referenceLimited",
        "budgetLimited", "blocks2", "blocks4", "blocks8", "blocks16", "blocks32", "blocks64", "blocks128Plus", "eligibleCandidates"];
    public int Pixels { get; init; }
    public double ViewportBound { get; init; }
    public int UsableNodes { get; init; }
    public int ReferenceLength { get; init; }
    public double MaxRadius { get; init; }
    public bool GpuCountersAvailable { get; init; }
    public long ScalarIterations { get; set; }
    public long SkippedIterations { get; set; }
    public long Rebases { get; set; }
    public long[] Counters { get; } = new long[CounterCount];

    public static bool Enabled => Environment.GetEnvironmentVariable("MANDELBROT_BLA_PROFILE") switch
    {
        null or "0" => false,
        "1" => true,
        _ => throw new ArgumentOutOfRangeException("MANDELBROT_BLA_PROFILE")
    };


}
