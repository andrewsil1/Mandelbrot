namespace MandelbrotGpu;

// Returned by the renderer so the UI can display both the finished image and
// the precision/repair diagnostics that explain deep-zoom quality.
public sealed record RenderResult(
    int[] Pixels,
    RenderMode Mode,
    int InitialGlitchCount,
    int RepairedCount,
    int UnresolvedGlitchCount,
    int ReferencePasses,
    int FinalRepairLimit,
    RenderTimings Timings,
    RenderValidation? Validation)
{
    public int Float64GlitchCount { get; init; }
    public bool UsedDoubleDoubleFallback { get; init; }
}
