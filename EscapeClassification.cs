namespace MandelbrotGpu;

internal static class EscapeClassification
{
    // Non-negative values are normal escape iteration counts. Negative sentinel
    // values keep special classifications out of the histogram.
    public const int Interior = -1;

    // A perturbation glitch means the GPU orbit became numerically suspect.
    // These pixels are either resolved by another reference orbit or repaired
    // with direct MPFR evaluation on the CPU.
    public const int Glitch = -2;
}
